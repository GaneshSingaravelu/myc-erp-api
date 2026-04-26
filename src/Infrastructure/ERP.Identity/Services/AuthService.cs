using System.IdentityModel.Tokens.Jwt;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Abstractions;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Configuration;
using ERP.Identity.Entities;
using ERP.SharedKernel.Results;
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
///
/// PERSISTENCE BOUNDARY:
/// ======================
/// AuthService talks to the database ONLY through the repository abstractions
/// (IUserRepository, IUserSessionRepository, IUserPasswordHistoryRepository,
/// ITenantRepository) and commits via IMasterUnitOfWork. It does NOT reference
/// EF Core or MasterDbContext directly — Clean Architecture's dependency rule.
/// </summary>
public class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IUserSessionRepository _sessions;
    private readonly IUserPasswordHistoryRepository _passwordHistory;
    private readonly ITenantRepository _tenants;
    private readonly IMasterUnitOfWork _unitOfWork;
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
        IUserRepository users,
        IUserSessionRepository sessions,
        IUserPasswordHistoryRepository passwordHistory,
        ITenantRepository tenants,
        IMasterUnitOfWork unitOfWork,
        ITokenService tokenService,
        IMemoryCache cache,
        IOptions<JwtSettings> jwtSettings,
        ILogger<AuthService> logger)
    {
        _users = users;
        _sessions = sessions;
        _passwordHistory = passwordHistory;
        _tenants = tenants;
        _unitOfWork = unitOfWork;
        _tokenService = tokenService;
        _cache = cache;
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
    }

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, string tenantId)
    {
        // Step 1: Find user by normalized username (eager-load roles + permissions for token claims)
        var normalizedUserName = request.UserName.ToUpperInvariant();
        var user = await _users.GetByNormalizedUserNameWithRolesAndPermissionsAsync(normalizedUserName);

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
            await _unitOfWork.SaveChangesAsync();

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
        Tenant? resolvedTenant;
        if (Guid.TryParse(tenantId, out var tid))
        {
            resolvedTenant = await _tenants.GetByIdAsync(tid);
        }
        else
        {
            resolvedTenant = await _tenants.GetByCodeAsync(tenantId);
        }

        if (resolvedTenant is null)
        {
            _logger.LogWarning("Login failed: tenant '{TenantId}' not found", tenantId);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidTenant", "Tenant not found."));
        }

        // Step 5b: Cross-tenant isolation gate — verify the user is an active member of
        // this tenant via the UserTenants junction. WITHOUT this check, valid credentials
        // would let any user authenticate into ANY existing tenant by varying the
        // X-Tenant-Id header (cross-tenant authentication / isolation break).
        //
        // The error code is intentionally generic ("InvalidCredentials") rather than
        // "NotAssignedToTenant" so we don't leak tenant-membership info to attackers
        // probing valid credential / tenant pairings.
        if (!await _users.IsAssignedToTenantAsync(user.Id, resolvedTenant.Id))
        {
            _logger.LogWarning(
                "Login denied: user '{UserName}' is not an active member of tenant '{TenantCode}'",
                user.UserName,
                resolvedTenant.Code);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidCredentials", "Invalid username or password."));
        }

        // Step 6: Generate tokens — JTI must match between token and session for revocation checks.
        // Token claim always uses tenant Code (human-readable), regardless of how the caller addressed it.
        var jti = Guid.NewGuid().ToString();
        var refreshToken = _tokenService.GenerateRefreshToken();
        var accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, resolvedTenant.Code, jti);

        // Step 7: Create session record (multi-device support)
        var session = new UserSession
        {
            UserId = user.Id,
            TenantId = resolvedTenant.Id,
            RefreshToken = refreshToken,
            RefreshTokenExpiry = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            AccessTokenJti = jti,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            CreatedAt = DateTime.UtcNow
        };

        await _sessions.AddAsync(session);
        user.LastLoginAt = DateTime.UtcNow;
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("User '{UserName}' logged in successfully for tenant '{TenantId}'", user.UserName, resolvedTenant.Code);

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

        if (await _users.ExistsByNormalizedUserNameAsync(normalizedUserName))
            return Result.Failure<UserDto>(Error.Conflict("Auth.UserNameExists", $"Username '{request.UserName}' is already taken."));

        if (await _users.ExistsByNormalizedEmailAsync(normalizedEmail))
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

        await _users.AddAsync(user);
        await _unitOfWork.SaveChangesAsync();

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
        var session = await _sessions.FindActiveByRefreshTokenAsync(userId, request.RefreshToken, now);

        if (session is null)
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "Invalid or expired refresh token."));

        // SECURITY: Bind the access token's jti to this session's AccessTokenJti.
        // Without this, an attacker holding access token A from session-1 and refresh
        // token B from session-2 (same user, different devices/sessions) could mix-and-match.
        // The session was found by refresh token alone — we MUST also verify the access
        // token presented in the same request actually belongs to that session.
        var accessTokenJti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrEmpty(accessTokenJti) || accessTokenJti != session.AccessTokenJti)
        {
            session.IsRevoked = true;
            session.RevokedAt = now;
            session.RevokedReason = "JtiMismatch";
            await _unitOfWork.SaveChangesAsync();
            InvalidateJtiCache(session.AccessTokenJti);

            _logger.LogWarning(
                "Refresh rejected for user {UserId}: access-token jti '{TokenJti}' does not match session jti '{SessionJti}'",
                userId, accessTokenJti, session.AccessTokenJti);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "Token pair mismatch. Please log in again."));
        }

        // SECURITY: Enforce tenant binding — session.TenantId must match the requested tenant.
        // This prevents token replay across tenants (manipulating X-Tenant-Id header).
        var resolvedRequestedTenantId = await ResolveTenantIdAsync(tenantId);
        if (resolvedRequestedTenantId is null || session.TenantId != resolvedRequestedTenantId.Value)
        {
            session.IsRevoked = true;
            session.RevokedAt = now;
            session.RevokedReason = "TenantMismatch";
            await _unitOfWork.SaveChangesAsync();
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
                await _unitOfWork.SaveChangesAsync();
                InvalidateJtiCache(session.AccessTokenJti);

                _logger.LogWarning("Refresh rejected for user {UserId}: token tenant_id claim doesn't match session", userId);
                return Result.Failure<LoginResponse>(Error.Validation("Auth.TenantMismatch", "Token tenant does not match session tenant."));
            }
        }

        // Load user with roles and permissions
        var user = await _users.GetByIdWithRolesAndPermissionsAsync(userId);

        if (user is null || !user.IsActive)
        {
            session.IsRevoked = true;
            session.RevokedAt = now;
            session.RevokedReason = "UserInactive";
            await _unitOfWork.SaveChangesAsync();
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
            await _unitOfWork.SaveChangesAsync();
            InvalidateJtiCache(session.AccessTokenJti);

            _logger.LogWarning("Refresh rejected for user '{UserName}': SecurityStamp mismatch", user.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.SecurityStampChanged", "Security credentials have changed. Please log in again."));
        }

        // Generate new tokens (token rotation) — derive tenant from session, NOT from caller header
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var permissions = user.UserRoles.SelectMany(ur => ur.Role.RolePermissions).Select(rp => rp.Permission.Code).Distinct().ToList();

        // Resolve tenant code from session's TenantId for the token claim
        var sessionTenant = await _tenants.GetByIdAsync(session.TenantId);
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

        await _unitOfWork.SaveChangesAsync();
        InvalidateJtiCache(oldJti);

        return Result.Success(new LoginResponse(
            AccessToken: newAccessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: now.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            User: MapToDto(user, roles)));
    }

    public async Task<Result> LogoutAsync(Guid userId, string refreshToken)
    {
        var session = await _sessions.FindNonRevokedByRefreshTokenAsync(userId, refreshToken);

        if (session is null)
            return Result.Success(); // Already logged out or invalid — idempotent

        session.IsRevoked = true;
        session.RevokedAt = DateTime.UtcNow;
        session.RevokedReason = "Logout";

        await _unitOfWork.SaveChangesAsync();

        // Immediately invalidate the JTI cache so the access token is rejected on next request.
        // Without this, the token would remain valid for up to the cache TTL (2 min).
        InvalidateJtiCache(session.AccessTokenJti);

        _logger.LogInformation("Session revoked for user {UserId}", userId);

        return Result.Success();
    }

    public async Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        var user = await _users.GetByIdAsync(userId);
        if (user is null)
            return Result.Failure(Error.NotFound("User", userId));

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            return Result.Failure(Error.Validation("Auth.InvalidPassword", "Current password is incorrect."));

        // Check password history — prevent reuse of last N passwords
        var recentPasswords = await _passwordHistory.GetRecentForUserAsync(userId, PasswordHistoryCount);

        if (recentPasswords.Any(h => BCrypt.Net.BCrypt.Verify(newPassword, h.PasswordHash)))
            return Result.Failure(Error.Validation("Auth.PasswordReused",
                $"Cannot reuse any of your last {PasswordHistoryCount} passwords."));

        // Also check the current password hash (don't allow changing to the same password)
        if (BCrypt.Net.BCrypt.Verify(newPassword, user.PasswordHash))
            return Result.Failure(Error.Validation("Auth.PasswordReused", "New password cannot be the same as the current password."));

        // Store old password in history
        await _passwordHistory.AddAsync(new UserPasswordHistory
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
        var activeSessions = await _sessions.GetActiveSessionsForUserAsync(userId);

        foreach (var session in activeSessions)
        {
            session.IsRevoked = true;
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = "PasswordChanged";
        }

        await _unitOfWork.SaveChangesAsync();

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
            var exists = await _tenants.ExistsByIdAsync(guid);
            return exists ? guid : null;
        }

        var tenant = await _tenants.GetByCodeAsync(tenantIdOrCode);
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
