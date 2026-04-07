using ERP.Identity.Configuration;
using ERP.Identity.DTOs;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
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
    private readonly JwtSettings _jwtSettings;
    private readonly ILogger<AuthService> _logger;

    private const int MaxFailedAttempts = 5;
    private const int LockoutMinutes = 15;
    private const int PasswordHistoryCount = 5;

    public AuthService(
        MasterDbContext context,
        ITokenService tokenService,
        IOptions<JwtSettings> jwtSettings,
        ILogger<AuthService> logger)
    {
        _context = context;
        _tokenService = tokenService;
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
        Guid resolvedTenantId;
        if (Guid.TryParse(tenantId, out var tid))
        {
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

        // Step 6: Generate tokens
        var accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, tenantId);
        var refreshToken = _tokenService.GenerateRefreshToken();

        // Step 7: Create session record (multi-device support)
        var session = new UserSession
        {
            UserId = user.Id,
            TenantId = resolvedTenantId,
            RefreshToken = refreshToken,
            RefreshTokenExpiry = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            AccessTokenJti = Guid.NewGuid().ToString(),
            ExpiresAt = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            CreatedAt = DateTime.UtcNow
        };

        await _context.Set<UserSession>().AddAsync(session);
        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("User '{UserName}' logged in successfully", user.UserName);

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

        // Find matching session by refresh token
        var session = await _context.Set<UserSession>()
            .FirstOrDefaultAsync(s => s.UserId == userId
                && s.RefreshToken == request.RefreshToken
                && !s.IsRevoked
                && s.RefreshTokenExpiry > DateTime.UtcNow);

        if (session is null)
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "Invalid or expired refresh token."));

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
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = "UserInactive";
            await _context.SaveChangesAsync();
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "User not found or inactive."));
        }

        // SecurityStamp validation — if password/security changed, reject the session
        var securityStampClaim = principal.FindFirst("security_stamp")?.Value;
        if (securityStampClaim is not null && securityStampClaim != user.SecurityStamp)
        {
            session.IsRevoked = true;
            session.RevokedAt = DateTime.UtcNow;
            session.RevokedReason = "SecurityStampChanged";
            await _context.SaveChangesAsync();

            _logger.LogWarning("Refresh rejected for user '{UserName}': SecurityStamp mismatch", user.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.SecurityStampChanged", "Security credentials have changed. Please log in again."));
        }

        // Generate new tokens (token rotation)
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var permissions = user.UserRoles.SelectMany(ur => ur.Role.RolePermissions).Select(rp => rp.Permission.Code).Distinct().ToList();

        var newAccessToken = _tokenService.GenerateAccessToken(user, roles, permissions, tenantId);
        var newRefreshToken = _tokenService.GenerateRefreshToken();

        // Update session with new tokens
        session.RefreshToken = newRefreshToken;
        session.RefreshTokenExpiry = DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays);
        session.AccessTokenJti = Guid.NewGuid().ToString();
        session.LastAccessedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Result.Success(new LoginResponse(
            AccessToken: newAccessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
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

        _logger.LogInformation("Password changed for user {UserId}. {SessionCount} sessions revoked.", userId, activeSessions.Count);
        return Result.Success();
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
