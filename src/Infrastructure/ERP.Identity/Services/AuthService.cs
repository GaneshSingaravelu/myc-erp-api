using ERP.Identity.Configuration;
using ERP.Identity.DTOs;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ERP.Identity.Services;

/// <summary>
/// AUTH SERVICE IMPLEMENTATION — Handles login, register, token refresh, logout, and password change.
///
/// SESSION-BASED AUTH FLOW:
/// =========================
/// Login  → Creates UserSession record → returns access + refresh tokens
/// Refresh → Finds session by refresh token → rotates tokens → updates session
/// Logout → Revokes the specific session
/// Password Change → Revokes ALL sessions (SecurityStamp rotation)
///
/// SECURITY FEATURES:
/// ===================
/// - Account lockout: 5 failed attempts → 15 min lock
/// - SecurityStamp: random GUID, changes on password/security events, validated on refresh
/// - Password history: prevents reuse of last 5 passwords
/// - Token rotation: each refresh generates a new refresh token (old one is invalidated)
/// - Multi-device: each login creates a separate session
/// </summary>
public class AuthService : IAuthService
{
    private readonly MasterDbContext _context;
    private readonly ITokenService _tokenService;
    private readonly IMemoryCache _cache;
    private readonly JwtSettings _jwtSettings;
    private readonly ILogger<AuthService> _logger;

    private const int MaxFailedAttempts = 5;
    private const int LockoutMinutes = 15;
    private const int PasswordHistoryCount = 5;

    /// <summary>Cache key prefix — must match the key used in OnTokenValidated (IdentityServiceExtensions)</summary>
    private const string JtiCacheKeyPrefix = "jti_valid:";

    public AuthService(
        MasterDbContext context,
        ITokenService tokenService,
        IMemoryCache cache,
        IOptions<JwtSettings> jwtSettings,
        ILogger<AuthService> logger)
    {
        _context = context;
        _tokenService = tokenService;
        _cache = cache;
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
    }

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, string tenantId)
    {
        // Step 1: Find user by normalized username
        var normalizedUserName = request.UserName.ToUpperInvariant();
        var user = await _context.Set<AppUser>()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName);

        if (user is null)
        {
            _logger.LogWarning("Login failed: user '{UserName}' not found", request.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidCredentials", "Invalid username or password."));
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login failed: user '{UserName}' is inactive", request.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.AccountInactive", "Account is inactive. Contact administrator."));
        }

        // Step 2: Check lockout
        if (user.IsLocked && user.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow)
        {
            _logger.LogWarning("Login failed: user '{UserName}' is locked out until {LockoutEnd}", request.UserName, user.LockoutEnd);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.AccountLocked", "Account is locked. Try again later."));
        }

        // Auto-unlock if lockout expired
        if (user.IsLocked && user.LockoutEnd.HasValue && user.LockoutEnd <= DateTime.UtcNow)
        {
            user.IsLocked = false;
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
        }

        // Step 3: Verify password
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= MaxFailedAttempts)
            {
                user.IsLocked = true;
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                _logger.LogWarning("User '{UserName}' locked out after {Count} failed attempts", request.UserName, user.AccessFailedCount);
            }
            await _context.SaveChangesAsync();

            _logger.LogWarning("Login failed: invalid password for '{UserName}'", request.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidCredentials", "Invalid username or password."));
        }

        // Reset failed attempts on successful login
        user.AccessFailedCount = 0;
        user.IsLocked = false;
        user.LockoutEnd = null;

        // Step 4: Get roles and permissions (filter by tenant if needed)
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

        // Step 5: Resolve tenant — look up by identifier string or GUID
        // SECURITY: Both paths validate tenant existence in the database.
        // Without the GUID path check, an attacker could create sessions
        // for non-existent tenants by passing arbitrary GUIDs.
        Guid resolvedTenantId;
        if (Guid.TryParse(tenantId, out var tid))
        {
            var tenantExists = await _context.Set<Tenant>().AnyAsync(t => t.Id == tid);
            if (!tenantExists)
            {
                _logger.LogWarning("Login failed: tenant GUID '{TenantId}' not found", tenantId);
                return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidTenant", "Tenant not found."));
            }
            resolvedTenantId = tid;
        }
        else
        {
            var tenant = await _context.Set<Tenant>()
                .FirstOrDefaultAsync(t => t.Code == tenantId);
            if (tenant is null)
            {
                _logger.LogWarning("Login failed: tenant '{TenantId}' not found", tenantId);
                return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidTenant", "Tenant not found."));
            }
            resolvedTenantId = tenant.Id;
        }

        // Step 6: Generate tokens — JTI must match between token and session for revocation checks
        var jti = Guid.NewGuid().ToString();
        var refreshToken = _tokenService.GenerateRefreshToken();

        // Resolve tenant identifier for the token claim (use Code, not GUID)
        var tenantForClaim = tenantId;
        if (Guid.TryParse(tenantId, out _))
        {
            var tenantEntity = await _context.Set<Tenant>().FindAsync(resolvedTenantId);
            tenantForClaim = tenantEntity?.Code ?? tenantId;
        }

        var accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, tenantForClaim, jti);

        // Step 7: Create session record (multi-device support)
        var session = new UserSession
        {
            UserId = user.Id,
            TenantId = resolvedTenantId,
            RefreshToken = refreshToken,
            RefreshTokenExpiry = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            AccessTokenJti = jti,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            CreatedAt = DateTime.UtcNow
        };

        await _context.Set<UserSession>().AddAsync(session);
        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("User '{UserName}' logged in successfully for tenant '{TenantId}'", user.UserName, tenantForClaim);

        return Result.Success(new LoginResponse(
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresAt: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            User: MapToDto(user, roles)));
    }

    public async Task<Result<UserDto>> RegisterAsync(RegisterRequest request)
    {
        var normalizedUserName = request.UserName.ToUpperInvariant();
        var normalizedEmail = request.Email.ToUpperInvariant();

        if (await _context.Set<AppUser>().AnyAsync(u => u.NormalizedUserName == normalizedUserName))
            return Result.Failure<UserDto>(Error.Conflict("Auth.UserNameExists", $"Username '{request.UserName}' is already taken."));

        if (await _context.Set<AppUser>().AnyAsync(u => u.NormalizedEmail == normalizedEmail))
            return Result.Failure<UserDto>(Error.Conflict("Auth.EmailExists", $"Email '{request.Email}' is already registered."));

        var user = new AppUser
        {
            UserName = request.UserName,
            NormalizedUserName = normalizedUserName,
            Email = request.Email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true
        };

        await _context.Set<AppUser>().AddAsync(user);
        await _context.SaveChangesAsync();

        _logger.LogInformation("User '{UserName}' registered successfully", user.UserName);
        return Result.Success(MapToDto(user, []));
    }

    public async Task<Result<LoginResponse>> RefreshTokenAsync(RefreshTokenRequest request, string tenantId)
    {
        // Extract user from expired token
        var principal = _tokenService.GetPrincipalFromExpiredToken(request.AccessToken);
        if (principal is null)
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidToken", "Invalid access token."));

        var userIdClaim = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidToken", "Invalid token claims."));

        // Find matching session — enforce ALL security checks:
        // 1. Correct user + refresh token
        // 2. Not revoked
        // 3. Refresh token not expired
        // 4. Absolute session expiry not exceeded (prevents indefinite refresh)
        var now = DateTime.UtcNow;
        var session = await _context.Set<UserSession>()
            .FirstOrDefaultAsync(s => s.UserId == userId
                && s.RefreshToken == request.RefreshToken
                && !s.IsRevoked
                && s.RefreshTokenExpiry > now
                && s.ExpiresAt > now);

        if (session is null)
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "Invalid or expired refresh token."));

        // SECURITY: Enforce tenant binding — session.TenantId must match the requested tenant
        // This prevents token replay across tenants (manipulating X-Tenant-Id header)
        var resolvedRequestedTenantId = await ResolveTenantIdAsync(tenantId);
        if (resolvedRequestedTenantId is null || session.TenantId != resolvedRequestedTenantId.Value)
        {
            session.IsRevoked = true;
            session.RevokedAt = now;
            session.RevokedReason = "TenantMismatch";
            await _context.SaveChangesAsync();
            InvalidateJtiCache(session.AccessTokenJti);

            _logger.LogWarning("Refresh rejected for user {UserId}: tenant mismatch (session={SessionTenant}, requested={RequestedTenant})",
                userId, session.TenantId, tenantId);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.TenantMismatch", "Session tenant does not match requested tenant."));
        }

        // SECURITY: Cross-check token's tenant_id claim against session tenant
        var tokenTenantClaim = principal.FindFirst("tenant_id")?.Value;
        if (tokenTenantClaim is not null)
        {
            var tokenTenantId = await ResolveTenantIdAsync(tokenTenantClaim);
            if (tokenTenantId is null || tokenTenantId.Value != session.TenantId)
            {
                session.IsRevoked = true;
                session.RevokedAt = now;
                session.RevokedReason = "TokenTenantMismatch";
                await _context.SaveChangesAsync();
                InvalidateJtiCache(session.AccessTokenJti);

                _logger.LogWarning("Refresh rejected for user {UserId}: token tenant_id claim doesn't match session", userId);
                return Result.Failure<LoginResponse>(Error.Validation("Auth.TenantMismatch", "Token tenant does not match session tenant."));
            }
        }

        // Load user with roles and permissions
        var user = await _context.Set<AppUser>()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null || !user.IsActive)
        {
            session.IsRevoked = true;
            session.RevokedAt = now;
            session.RevokedReason = "UserInactive";
            await _context.SaveChangesAsync();
            InvalidateJtiCache(session.AccessTokenJti);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "User not found or inactive."));
        }

        // SecurityStamp validation — if password/security changed, reject the session
        var securityStampClaim = principal.FindFirst("security_stamp")?.Value;
        if (securityStampClaim is not null && securityStampClaim != user.SecurityStamp)
        {
            session.IsRevoked = true;
            session.RevokedAt = now;
            session.RevokedReason = "SecurityStampChanged";
            await _context.SaveChangesAsync();
            InvalidateJtiCache(session.AccessTokenJti);

            _logger.LogWarning("Refresh rejected for user '{UserName}': SecurityStamp mismatch", user.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.SecurityStampChanged", "Security credentials have changed. Please log in again."));
        }

        // Generate new tokens (token rotation) — derive tenant from session, NOT from caller header
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var permissions = user.UserRoles.SelectMany(ur => ur.Role.RolePermissions).Select(rp => rp.Permission.Code).Distinct().ToList();

        // Resolve tenant code from session's TenantId for the token claim
        var sessionTenant = await _context.Set<Tenant>().FindAsync(session.TenantId);
        var tenantCode = sessionTenant?.Code ?? tenantId;

        var newJti = Guid.NewGuid().ToString();
        var newAccessToken = _tokenService.GenerateAccessToken(user, roles, permissions, tenantCode, newJti);
        var newRefreshToken = _tokenService.GenerateRefreshToken();

        // Invalidate old JTI before rotating — the old access token should be rejected immediately
        var oldJti = session.AccessTokenJti;

        // Update session with new tokens (refresh token expiry extends, but absolute ExpiresAt does NOT)
        session.RefreshToken = newRefreshToken;
        session.RefreshTokenExpiry = now.AddDays(_jwtSettings.RefreshTokenExpirationDays);
        session.AccessTokenJti = newJti;
        session.LastAccessedAt = now;

        await _context.SaveChangesAsync();
        InvalidateJtiCache(oldJti);

        return Result.Success(new LoginResponse(
            AccessToken: newAccessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: now.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            User: MapToDto(user, roles)));
    }

    public async Task<Result> LogoutAsync(Guid userId, string refreshToken)
    {
        var session = await _context.Set<UserSession>()
            .FirstOrDefaultAsync(s => s.UserId == userId
                && s.RefreshToken == refreshToken
                && !s.IsRevoked);

        if (session is null)
            return Result.Success(); // Already logged out or invalid — idempotent

        session.IsRevoked = true;
        session.RevokedAt = DateTime.UtcNow;
        session.RevokedReason = "Logout";

        await _context.SaveChangesAsync();

        // Immediately invalidate the JTI cache so the access token is rejected on next request
        // Without this, the token would remain valid for up to the cache TTL (2 min)
        InvalidateJtiCache(session.AccessTokenJti);

        _logger.LogInformation("Session revoked for user {UserId}", userId);

        return Result.Success();
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        var user = await _context.Set<AppUser>().FindAsync(userId);
        if (user is null)
            return Result.Failure(Error.NotFound("User", userId));

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            return Result.Failure(Error.Validation("Auth.InvalidPassword", "Current password is incorrect."));

        // Check password history — prevent reuse of last N passwords
        var recentPasswords = await _context.Set<UserPasswordHistory>()
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.ChangedAt)
            .Take(PasswordHistoryCount)
            .ToListAsync();

        if (recentPasswords.Any(h => BCrypt.Net.BCrypt.Verify(newPassword, h.PasswordHash)))
            return Result.Failure(Error.Validation("Auth.PasswordReused",
                $"Cannot reuse any of your last {PasswordHistoryCount} passwords."));

        // Also check the current password hash (don't allow changing to the same password)
        if (BCrypt.Net.BCrypt.Verify(newPassword, user.PasswordHash))
            return Result.Failure(Error.Validation("Auth.PasswordReused", "New password cannot be the same as the current password."));

        // Store old password in history
        _context.Set<UserPasswordHistory>().Add(new UserPasswordHistory
        {
            UserId = user.Id,
            PasswordHash = user.PasswordHash,
            ChangedAt = DateTime.UtcNow
        });

        // Update password and rotate SecurityStamp
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.LastPasswordChangedAt = DateTime.UtcNow;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.MustChangePassword = false;

        // Revoke ALL active sessions (force re-login on all devices)
        var activeSessions = await _context.Set<UserSession>()
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .ToListAsync();

        foreach (var session in activeSessions)
        {
            session.IsRevoked = true;
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = "PasswordChanged";
        }

        await _context.SaveChangesAsync();

        // Immediately invalidate all JTI cache entries so access tokens are rejected on next request
        foreach (var session in activeSessions)
            InvalidateJtiCache(session.AccessTokenJti);

        _logger.LogInformation("Password changed for user {UserId}. {SessionCount} sessions revoked.", userId, activeSessions.Count);
        return Result.Success();
    }

    /// <summary>
    /// Immediately removes a JTI from the validation cache so the corresponding access token
    /// is rejected on the next request. Without this, revoked tokens would remain valid
    /// for up to the cache TTL (2 minutes) — unacceptable for security-critical operations
    /// like logout and password change.
    ///
    /// The cache key format must match OnTokenValidated in IdentityServiceExtensions.
    /// </summary>
    private void InvalidateJtiCache(string jti)
    {
        _cache.Remove($"{JtiCacheKeyPrefix}{jti}");
    }

    /// <summary>
    /// Resolves a tenant identifier (GUID string or Code) to the canonical Tenant.Id.
    /// Returns null if the tenant doesn't exist.
    /// </summary>
    private async Task<Guid?> ResolveTenantIdAsync(string tenantIdOrCode)
    {
        if (Guid.TryParse(tenantIdOrCode, out var guid))
        {
            var exists = await _context.Set<Tenant>().AnyAsync(t => t.Id == guid);
            return exists ? guid : null;
        }

        var tenant = await _context.Set<Tenant>()
            .FirstOrDefaultAsync(t => t.Code == tenantIdOrCode);
        return tenant?.Id;
    }

    private static UserDto MapToDto(AppUser user, IList<string> roles) => new(
        Id: user.Id,
        UserName: user.UserName,
        Email: user.Email,
        FirstName: user.FirstName,
        LastName: user.LastName,
        FullName: user.FullName,
        IsActive: user.IsActive,
        LastLoginAt: user.LastLoginAt,
        Roles: roles);
}
