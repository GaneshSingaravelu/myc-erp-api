using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using ERP.Application.Abstractions.Email;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Abstractions;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Configuration;
using ERP.Identity.Entities;
using ERP.SharedKernel.Results;
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
/// R2-DEV-03A — VERSIONED DEVICE SESSIONS:
/// - Refresh tokens are stored and looked up only as keyed HMAC-SHA-256 digests.
/// - Each UserSession is a device session FAMILY; RotationVersion is an optimistic-concurrency
///   token incremented by every rotation AND every revocation. A concurrent stale write fails
///   (DbUpdateConcurrencyException) instead of overwriting a newer state.
/// - Rotation updates the session and inserts the consumed-token record in ONE SaveChanges
///   (one transaction). New tokens are returned only after that commit succeeds.
/// - Revocations re-read and re-apply on a conflict, so a revocation always wins against a
///   concurrent refresh, and a refresh can never restore a revoked session.
/// - A consumed token presented with its paired access token is a DUPLICATE within
///   RefreshTokenSettings.DuplicateWindowSeconds (rejected, nothing revoked) and REUSE after it
///   (that one session family is revoked). Anything else is simply rejected.
/// - Access tokens are validated against the database on every request (no JTI cache), so a
///   revocation on one instance is effective on all instances immediately.
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
    private readonly IRefreshTokenHasher _refreshTokenHasher;
    private readonly IEmailService _emailService;
    private readonly JwtSettings _jwtSettings;
    private readonly RefreshTokenSettings _refreshTokenSettings;
    private readonly ILogger<AuthService> _logger;

    private const int MaxFailedAttempts = 5;
    private const int LockoutMinutes = 15;
    private const int PasswordHistoryCount = 5;

    /// <summary>Attempts to apply a revocation when concurrent session writes keep conflicting.</summary>
    private const int MaxRevocationAttempts = 5;

    public AuthService(
        IUserRepository users,
        IUserSessionRepository sessions,
        IUserPasswordHistoryRepository passwordHistory,
        ITenantRepository tenants,
        IMasterUnitOfWork unitOfWork,
        ITokenService tokenService,
        IRefreshTokenHasher refreshTokenHasher,
        IEmailService emailService,
        IOptions<JwtSettings> jwtSettings,
        IOptions<RefreshTokenSettings> refreshTokenSettings,
        ILogger<AuthService> logger)
    {
        _users = users;
        _sessions = sessions;
        _passwordHistory = passwordHistory;
        _tenants = tenants;
        _unitOfWork = unitOfWork;
        _tokenService = tokenService;
        _refreshTokenHasher = refreshTokenHasher;
        _emailService = emailService;
        _jwtSettings = jwtSettings.Value;
        _refreshTokenSettings = refreshTokenSettings.Value;
        _logger = logger;
    }

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request, string tenantId)
    {
        // Step 0: Fail closed on a missing tenant identifier. The controller already rejects
        // it, but the service is the authoritative gate and must not rely on its callers.
        if (string.IsNullOrWhiteSpace(tenantId))
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidTenant", "Tenant not found."));

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

        // Step 4: Resolve tenant — look up by identifier string or GUID.
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

        // Step 4b: Cross-tenant isolation gate — verify the user is an active member of
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

        // Step 4c: SECURITY — An inactive tenant must not issue sessions. /api/v1/auth/login is
        // in TenantMiddleware's skip list, so the middleware's IsActive gate never runs for
        // login — this check is the ONLY place it is enforced, and it reads the database
        // directly (not the 30-minute middleware cache). It runs AFTER the membership gate so
        // a non-member gets the generic InvalidCredentials and cannot learn the tenant's status.
        if (!resolvedTenant.IsActive)
        {
            _logger.LogWarning("Login denied: tenant '{TenantCode}' is inactive", resolvedTenant.Code);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.TenantInactive", "Tenant account is inactive. Contact administrator."));
        }

        // Step 5: Build roles and permissions scoped to the resolved tenant.
        // SECURITY: AppUserRole carries a TenantId — a user can be Admin in Tenant A
        // and Viewer in Tenant B. Filtering here prevents JWT claims from leaking
        // roles/permissions from tenants the user did not authenticate into.
        var tenantUserRoles = user.UserRoles.Where(ur => ur.TenantId == resolvedTenant.Id).ToList();
        var roles = tenantUserRoles.Select(ur => ur.Role.Name).ToList();
        var permissions = tenantUserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToList();

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
            RefreshTokenHash = _refreshTokenHasher.Hash(refreshToken), // digest only, never plaintext
            RotationVersion = 0,
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
        if (string.IsNullOrWhiteSpace(tenantId))
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidTenant", "Tenant not found."));

        // Extract user from expired token
        var principal = _tokenService.GetPrincipalFromExpiredToken(request.AccessToken);
        if (principal is null)
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidToken", "Invalid access token."));

        // Read by JWT-native short name. GetPrincipalFromExpiredToken disables
        // inbound claim mapping (see TokenService), so "sub" stays "sub" rather
        // than being remapped to ClaimTypes.NameIdentifier.
        //
        // MIGRATION FALLBACK: tokens issued before commit 428d30c carried the user-id
        // as "nameid" (ClaimTypes.NameIdentifier serialised through the outbound map).
        // Fall back to that raw name so sessions survive the deploy. Remove this
        // fallback once the 7-day refresh-token TTL has elapsed post-deploy.
        var userIdClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst("nameid")?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidToken", "Invalid token claims."));

        // Find matching session — enforce ALL security checks:
        // 1. Correct user + refresh token
        // 2. Not revoked
        // 3. Refresh token not expired
        // 4. Absolute session expiry not exceeded (prevents indefinite refresh)
        var now = DateTime.UtcNow;
        var accessTokenJti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        if (string.IsNullOrEmpty(request.RefreshToken))
            return InvalidRefreshToken();

        // R2-DEV-03A: look the token up by its keyed digest; plaintext is never stored.
        var presentedHash = _refreshTokenHasher.Hash(request.RefreshToken);
        var session = await _sessions.FindActiveByRefreshTokenHashAsync(userId, presentedHash, now);

        if (session is null)
            return await RejectUnmatchedRefreshTokenAsync(userId, accessTokenJti, presentedHash, now);

        // SECURITY: Bind the access token's jti to this session's AccessTokenJti.
        // Without this, an attacker holding access token A from session-1 and refresh
        // token B from session-2 (same user, different devices/sessions) could mix-and-match.
        // The session was found by refresh token alone — we MUST also verify the access
        // token presented in the same request actually belongs to that session.
        //
        // Reads here use JWT-native short names ("jti", "sub") because
        // TokenService.GetPrincipalFromExpiredToken disables inbound claim mapping.
        // That keeps the claim contract explicit and immune to future changes in
        // JwtSecurityTokenHandler.DefaultInboundClaimTypeMap. The integration test
        // Refresh_WithRealJwtRoundTrip_FindsJtiClaim asserts the round-trip works
        // against the real handler, not just hand-built principals.
        if (string.IsNullOrEmpty(accessTokenJti) || accessTokenJti != session.AccessTokenJti)
        {
            await RevokeSessionAsync(session, "JtiMismatch", now);

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
            await RevokeSessionAsync(session, "TenantMismatch", now);

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
                await RevokeSessionAsync(session, "TokenTenantMismatch", now);

                _logger.LogWarning("Refresh rejected for user {UserId}: token tenant_id claim doesn't match session", userId);
                return Result.Failure<LoginResponse>(Error.Validation("Auth.TenantMismatch", "Token tenant does not match session tenant."));
            }
        }

        // SECURITY: The session's tenant must still exist and be active. Read from the
        // database on every refresh so a deactivation takes effect at the next refresh,
        // independent of TenantMiddleware's cached TenantInfo. When a refresh request reaches
        // this check while the session's tenant is missing or inactive, THAT session is revoked,
        // so re-activating the tenant later does not revive it. Deactivation itself does not
        // revoke sessions: a session that is not refreshed while the tenant is inactive stays
        // un-revoked and can be refreshed again if the tenant is re-activated.
        var sessionTenant = await _tenants.GetByIdAsync(session.TenantId);
        if (sessionTenant is null || !sessionTenant.IsActive)
        {
            await RevokeSessionAsync(session, "TenantInactive", now);

            _logger.LogWarning("Refresh rejected for user {UserId}: tenant {TenantId} is missing or inactive", userId, session.TenantId);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.TenantInactive", "Tenant account is inactive. Contact administrator."));
        }

        // Load user with roles and permissions
        var user = await _users.GetByIdWithRolesAndPermissionsAsync(userId);

        if (user is null || !user.IsActive)
        {
            await RevokeSessionAsync(session, "UserInactive", now);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "User not found or inactive."));
        }

        // SecurityStamp validation — if password/security changed, reject the session
        var securityStampClaim = principal.FindFirst("security_stamp")?.Value;
        if (securityStampClaim is not null && securityStampClaim != user.SecurityStamp)
        {
            await RevokeSessionAsync(session, "SecurityStampChanged", now);

            _logger.LogWarning("Refresh rejected for user '{UserName}': SecurityStamp mismatch", user.UserName);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.SecurityStampChanged", "Security credentials have changed. Please log in again."));
        }

        // SECURITY: Same membership gate as LoginAsync. Without it, removing a user from a
        // tenant (UserTenants.IsActive = false) would not stop them refreshing an existing
        // session for that tenant until its absolute expiry. Generic error, as at login.
        if (!await _users.IsAssignedToTenantAsync(user.Id, session.TenantId))
        {
            await RevokeSessionAsync(session, "TenantMembershipRevoked", now);

            _logger.LogWarning("Refresh rejected for user {UserId}: no active membership in tenant {TenantId}", userId, session.TenantId);
            return Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "Invalid or expired refresh token."));
        }

        // Generate new tokens (token rotation) — derive tenant from session, NOT from caller header.
        // SECURITY: Scope roles/permissions to the session's tenant, same as LoginAsync, so a user
        // with roles in multiple tenants never receives cross-tenant claims via token refresh.
        var tenantScopedRoles = user.UserRoles.Where(ur => ur.TenantId == session.TenantId).ToList();
        var roles = tenantScopedRoles.Select(ur => ur.Role.Name).ToList();
        var permissions = tenantScopedRoles.SelectMany(ur => ur.Role.RolePermissions).Select(rp => rp.Permission.Code).Distinct().ToList();
        // Tenant code comes only from the verified session tenant — never from the caller header.
        var tenantCode = sessionTenant.Code;

        var newJti = Guid.NewGuid().ToString();
        var newAccessToken = _tokenService.GenerateAccessToken(user, roles, permissions, tenantCode, newJti);
        var newRefreshToken = _tokenService.GenerateRefreshToken();

        // R2-DEV-03A — ATOMIC ROTATION.
        // The consumed-token record and the session update are committed by ONE SaveChanges
        // (one transaction). The session UPDATE carries "AND RotationVersion = @consumedVersion"
        // (concurrency token), so when two requests race on the same token exactly one UPDATE
        // matches; the loser gets a concurrency conflict, its transaction (history insert
        // included) rolls back, and it overwrites nothing. The unique TokenHash index on the
        // history table is a second guard against recording one token twice.
        var consumedVersion = session.RotationVersion;
        await _sessions.AddConsumedAsync(new ConsumedRefreshToken
        {
            SessionId = session.Id,
            UserId = session.UserId,
            TokenHash = presentedHash,
            AccessTokenJti = session.AccessTokenJti, // the pair being consumed
            RotationVersion = consumedVersion,
            ConsumedAt = now,
            RetainUntil = session.ExpiresAt, // live sessions never lose their history
        });

        // Update session with new tokens (refresh token expiry extends, but absolute ExpiresAt does NOT).
        // The old access token is rejected immediately: OnTokenValidated reads AccessTokenJti fresh.
        session.RefreshTokenHash = _refreshTokenHasher.Hash(newRefreshToken);
        session.RefreshTokenExpiry = now.AddDays(_jwtSettings.RefreshTokenExpirationDays);
        session.AccessTokenJti = newJti;
        session.LastAccessedAt = now;
        session.RotationVersion = consumedVersion + 1;

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            // Lost the race (another refresh rotated it, or a revocation landed first). Nothing
            // was written; no credentials are issued.
            _logger.LogWarning(
                "Refresh for user {UserId} lost a concurrent update on session {SessionId} (version {Version}); no tokens issued",
                userId, session.Id, consumedVersion);
            return InvalidRefreshToken();
        }
        catch (Exception ex) when (IsExpectedDatabaseFailure(ex))
        {
            // Commit outcome UNKNOWN: the rotation may or may not have been applied. The new
            // pair is discarded and never returned; the request is not retried. If it did commit,
            // a resend of the old pair is a duplicate inside the window and reuse after it.
            _logger.LogError(ex,
                "Refresh for user {UserId} on session {SessionId}: rotation not confirmed (commit outcome unknown); no tokens issued",
                userId, session.Id);
            return InvalidRefreshToken();
        }

        // Only reached after a confirmed commit.
        return Result.Success(new LoginResponse(
            AccessToken: newAccessToken,
            RefreshToken: newRefreshToken,
            ExpiresAt: now.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            User: MapToDto(user, roles)));
    }

    public async Task<Result> LogoutAsync(Guid userId, string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
            return Result.Success(); // nothing to revoke — idempotent

        var session = await _sessions.FindNonRevokedByRefreshTokenHashAsync(userId, _refreshTokenHasher.Hash(refreshToken));

        if (session is null)
            return Result.Success(); // Already logged out or invalid — idempotent

        // Wins against a concurrent refresh (re-read + re-apply on conflict). The access token is
        // rejected on the next request on every instance (OnTokenValidated reads the database).
        await RevokeSessionAsync(session, "Logout", DateTime.UtcNow);

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

        // Revoke ALL active sessions (force re-login on all devices). Saved together with the
        // password change; a concurrent refresh cannot keep a session alive (see SaveRevokingSessionsAsync).
        var activeSessions = await _sessions.GetActiveSessionsForUserAsync(userId);
        await SaveRevokingSessionsAsync(activeSessions, "PasswordChanged", DateTime.UtcNow);

        _logger.LogInformation("Password changed for user {UserId}. {SessionCount} sessions revoked.", userId, activeSessions.Count);
        return Result.Success();
    }

    /// <summary>
    /// Issues a password-reset token bound to the tenant the request was made through.
    ///
    /// R2-DEV-02B-3A — TENANT-BOUND RECOVERY:
    /// AppUser is a global identity with one password, but recovery is only offered through a
    /// tenant that is ACTIVE (read from the Master DB, not TenantMiddleware's cache) and in which
    /// the user holds an ACTIVE membership. The issued token is bound to that tenant's Id inside
    /// the stored hash (see <see cref="ComputeResetTokenHash"/>), so it can only be redeemed
    /// through the same tenant.
    ///
    /// Every refusal — unknown/inactive tenant, unknown/inactive user, no membership, or a
    /// Master DB failure while checking any of them — returns the SAME generic success and
    /// issues nothing, leaving any existing token untouched. A caller cannot tell an unknown
    /// account from a missing or inactive membership.
    ///
    /// R2-DEV-02B-3A C1 — PERSISTENCE AND DELIVERY FAILURES:
    /// A database failure while SAVING the token also returns the generic response. Its commit
    /// outcome is treated as UNKNOWN (an exception from SaveChanges does not prove the write was
    /// rolled back), so the plaintext token is discarded: it is never emailed and never returned.
    /// A token that did get stored without being delivered cannot be used — nobody holds its
    /// plaintext — and expires in 15 minutes, but it may have replaced an earlier outstanding
    /// token (single token per user). An email failure AFTER a successful save keeps the
    /// generic response; the stored token simply goes undelivered. Both are logged at Error with
    /// a per-call operation id and the trace id. Only recognisable database failures are
    /// absorbed; any other exception (a bug) still surfaces.
    ///
    /// Logs never contain the plaintext token, its hash, passwords or connection strings; the
    /// user is identified by Id, not email.
    /// </summary>
    public async Task<Result<ForgotPasswordResponse>> ForgotPasswordAsync(
        ForgotPasswordRequest request, string tenantId, bool exposeToken = false)
    {
        var generic = Result.Success(new ForgotPasswordResponse(GenericForgotPasswordMessage));
        var operationId = Guid.NewGuid().ToString("N");
        var traceId = Activity.Current?.TraceId.ToString();

        Tenant? tenant;
        AppUser? user;
        try
        {
            tenant = await ResolveRecoveryTenantAsync(tenantId);
            if (tenant is null || !tenant.IsActive)
            {
                _logger.LogInformation(
                    "ForgotPassword {OperationId}: tenant missing, unknown or inactive; no token issued (not disclosed to caller)",
                    operationId);
                return generic;
            }

            user = string.IsNullOrWhiteSpace(request.Email)
                ? null
                : await _users.GetByNormalizedEmailAsync(request.Email.ToUpperInvariant());

            // Always return success to prevent email enumeration (don't reveal whether the email exists)
            if (user is null || !user.IsActive)
            {
                _logger.LogInformation(
                    "ForgotPassword {OperationId}: no active user found for email (not disclosed to caller)", operationId);
                return generic;
            }

            if (!await _users.IsAssignedToTenantAsync(user.Id, tenant.Id))
            {
                _logger.LogInformation(
                    "ForgotPassword {OperationId}: user {UserId} is not an active member of tenant {TenantCode}; no token issued (not disclosed to caller)",
                    operationId, user.Id, tenant.Code);
                return generic;
            }
        }
        catch (Exception ex) when (IsExpectedDatabaseFailure(ex))
        {
            // FAIL CLOSED without disclosure: if tenant authority or membership cannot be
            // verified, no token is issued and the caller sees the same generic response.
            _logger.LogError(ex,
                "ForgotPassword {OperationId} (trace {TraceId}): Master DB failure while verifying tenant, account or membership; no token issued, generic response returned",
                operationId, traceId);
            return generic;
        }

        // Generate a cryptographically secure random token
        var plainToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('='); // base64url-safe

        // One token per user (single hash/expiry columns): issuing replaces any earlier token,
        // including one issued through another tenant.
        user.PasswordResetTokenHash = ComputeResetTokenHash(tenant.Id, plainToken);
        user.PasswordResetTokenExpiry = DateTime.UtcNow.AddMinutes(15);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex) when (IsExpectedDatabaseFailure(ex))
        {
            // COMMIT OUTCOME UNKNOWN. The write may have been applied (e.g. the connection broke
            // after the server committed) or not. Either way the plaintext is discarded here and
            // never delivered, so a stored-but-undelivered token is unusable; it is NOT removed.
            //
            // The tracked entity is deliberately NOT reverted. EF keeps the properties marked
            // Modified after a failed save, so setting the old values back would not undo
            // anything — and if anything later in this scope did save, it would write the OLD
            // hash back, which could revive an earlier token that was redeemed in the meantime.
            // Left as is, a later save could only write this NEW, undelivered (unusable) hash.

            _logger.LogError(ex,
                "ForgotPassword {OperationId} (trace {TraceId}): saving the reset token for user {UserId} in tenant {TenantCode} failed; commit outcome UNKNOWN (the token may or may not be stored, and may have replaced an earlier one). Token discarded and not delivered; generic response returned",
                operationId, traceId, user.Id, tenant.Code);
            return generic;
        }

        _logger.LogInformation(
            "ForgotPassword {OperationId}: PasswordResetToken issued for user {UserId} in tenant {TenantCode}",
            operationId, user.Id, tenant.Code);

        if (!exposeToken)
        {
            try
            {
                await _emailService.SendAsync(new EmailMessage(
                    To: request.Email,
                    Subject: "ERP — Password Reset",
                    HtmlBody: $"""
                        <p>Hi {user.FirstName},</p>
                        <p>We received a request to reset your password. Use the token below to set a new password:</p>
                        <p style="font-family:monospace;background:#f4f4f4;padding:12px;border-radius:4px;word-break:break-all;">{plainToken}</p>
                        <p>This token expires in 15 minutes. If you did not request this, please ignore this email.</p>
                        <p>— ERP System</p>
                        """,
                    PlainTextBody: $"Hi {user.FirstName},\n\nYour password reset token: {plainToken}\n\nExpires in 15 minutes."));
            }
            catch (Exception ex)
            {
                // The token IS stored but was not delivered. The caller still gets the generic
                // response; monitoring sees the Error. The user can simply request a new token.
                _logger.LogError(ex,
                    "ForgotPassword {OperationId} (trace {TraceId}): reset token stored for user {UserId} in tenant {TenantCode} but email delivery FAILED; generic response returned",
                    operationId, traceId, user.Id, tenant.Code);
            }
        }

        return Result.Success(new ForgotPasswordResponse(
            GenericForgotPasswordMessage,
            exposeToken ? plainToken : null));
    }

    /// <summary>
    /// True when the exception chain contains a recognisable database failure:
    /// <list type="bullet">
    /// <item>an ADO.NET <see cref="System.Data.Common.DbException"/> (SqlException, NpgsqlException)
    /// or a <see cref="TimeoutException"/>;</item>
    /// <item>an exception TYPE from EF Core, SqlClient or Npgsql (DbUpdateException,
    /// RetryLimitExceededException, …), matched by namespace name;</item>
    /// <item>any exception THROWN FROM INSIDE the SqlClient or Npgsql provider assemblies, whatever
    /// its type. SqlClient reports connection-pool exhaustion ("Timeout expired … obtaining a
    /// connection from the pool") as a plain InvalidOperationException with no inner exception;
    /// its <see cref="Exception.Source"/> is the throwing assembly.</item>
    /// </list>
    /// Matching by name keeps this service free of an EF Core / provider dependency (pinned by the
    /// IdentityServices_Should_Not_Depend_On_EfCore architecture test). Anything else — including
    /// an InvalidOperationException raised by application code or by EF Core itself (e.g. context
    /// misuse) — is treated as a bug and is not absorbed.
    /// </summary>
    private static bool IsExpectedDatabaseFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Data.Common.DbException or TimeoutException)
                return true;

            var ns = e.GetType().Namespace ?? string.Empty;
            if (ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || ns.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal)
                || ns.StartsWith("Npgsql", StringComparison.Ordinal))
                return true;

            if (IsThrownByDatabaseProvider(e))
                return true;
        }

        return false;
    }

    private static bool IsThrownByDatabaseProvider(Exception e)
    {
        var source = e.Source ?? string.Empty;
        return source.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal)
            || source.StartsWith("Core Microsoft SqlClient Data Provider", StringComparison.Ordinal)
            || source.StartsWith("Npgsql", StringComparison.Ordinal);
    }

    /// <summary>
    /// Redeems a tenant-bound password-reset token.
    ///
    /// R2-DEV-02B-3A — the tenant is re-read from the Master DB and must be active; the token
    /// must have been issued through THIS tenant (the binding is inside the hash, so a token
    /// from tenant A never matches under tenant B, even for a member of both); and the user's
    /// membership in this tenant must still be active. Tokens stored before tenant binding
    /// (unbound hashes) can never match and are rejected — there is no legacy fallback.
    ///
    /// Order matters for disclosure: the token is verified BEFORE expiry, so only a caller who
    /// holds the correct token can learn that it expired; anyone else gets the generic
    /// invalid-token error whatever the account's state. A Master DB failure propagates and
    /// the reset does not happen (fail closed).
    /// </summary>
    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request, string tenantId)
    {
        var invalid = Result.Failure(Error.Validation("Auth.InvalidResetToken", "Invalid or expired password reset token."));

        var tenant = await ResolveRecoveryTenantAsync(tenantId);
        if (tenant is null || !tenant.IsActive)
            return invalid;

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Token))
            return invalid;

        var normalizedEmail = request.Email.ToUpperInvariant();
        var user = await _users.GetByNormalizedEmailAsync(normalizedEmail);

        if (user is null || !user.IsActive
            || user.PasswordResetTokenHash is null
            || user.PasswordResetTokenExpiry is null)
            return invalid;

        if (!ResetTokenMatches(user.PasswordResetTokenHash, tenant.Id, request.Token))
            return invalid;

        if (user.PasswordResetTokenExpiry < DateTime.UtcNow)
        {
            // Clear the expired token
            user.PasswordResetTokenHash = null;
            user.PasswordResetTokenExpiry = null;
            await _unitOfWork.SaveChangesAsync();
            return Result.Failure(Error.Validation("Auth.ResetTokenExpired", "Password reset token has expired. Please request a new one."));
        }

        if (!await _users.IsAssignedToTenantAsync(user.Id, tenant.Id))
        {
            _logger.LogWarning("ResetPassword: user {UserId} is no longer an active member of tenant {TenantCode}", user.Id, tenant.Code);
            return invalid;
        }

        // Check password history
        var recentPasswords = await _passwordHistory.GetRecentForUserAsync(user.Id, PasswordHistoryCount);
        if (recentPasswords.Any(h => BCrypt.Net.BCrypt.Verify(request.NewPassword, h.PasswordHash)))
            return Result.Failure(Error.Validation("Auth.PasswordReused",
                $"Cannot reuse any of your last {PasswordHistoryCount} passwords."));

        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, user.PasswordHash))
            return Result.Failure(Error.Validation("Auth.PasswordReused", "New password cannot be the same as the current password."));

        // Archive old password
        await _passwordHistory.AddAsync(new UserPasswordHistory
        {
            UserId = user.Id,
            PasswordHash = user.PasswordHash,
            ChangedAt = DateTime.UtcNow
        });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.LastPasswordChangedAt = DateTime.UtcNow;
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.MustChangePassword = false;
        user.PasswordResetTokenHash = null;
        user.PasswordResetTokenExpiry = null;

        // Revoke all sessions — password changed outside of normal flow
        var activeSessions = await _sessions.GetActiveSessionsForUserAsync(user.Id);
        await SaveRevokingSessionsAsync(activeSessions, "PasswordReset", DateTime.UtcNow);

        _logger.LogInformation("Password reset for user {UserId}. {SessionCount} sessions revoked.", user.Id, activeSessions.Count);
        return Result.Success();
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private const string GenericForgotPasswordMessage = "If that email is registered, a reset link has been sent.";

    /// <summary>
    /// Domain-separated, versioned binding of a reset token to a tenant. The stored value keeps
    /// the existing format (lowercase SHA-256 hex, 64 chars, same column), so no schema change.
    /// The token is base64url (no ':'), and the tenant Id has a fixed "D" format, so the input
    /// is unambiguous. A pre-binding hash (SHA-256 of the bare token) can never equal this.
    /// </summary>
    private const string ResetTokenBindingPrefix = "erp-pwreset-v2:";

    private static string ComputeResetTokenHash(Guid tenantId, string token) =>
        ComputeSha256Hash($"{ResetTokenBindingPrefix}{tenantId:D}:{token}");

    private static bool ResetTokenMatches(string storedHash, Guid tenantId, string suppliedToken)
    {
        var supplied = System.Text.Encoding.ASCII.GetBytes(ComputeResetTokenHash(tenantId, suppliedToken));
        var stored = System.Text.Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(supplied, stored);
    }

    /// <summary>
    /// Resolves the recovery tenant from the Master DB (authoritative — never TenantMiddleware's
    /// cache). Accepts a tenant Code or Id, exactly as login does. Returns null when the value is
    /// blank or names no tenant. Activity is checked by the caller.
    /// </summary>
    private async Task<Tenant?> ResolveRecoveryTenantAsync(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return null;

        return Guid.TryParse(tenantId, out var id)
            ? await _tenants.GetByIdAsync(id)
            : await _tenants.GetByCodeAsync(tenantId);
    }

    private static Result<LoginResponse> InvalidRefreshToken() =>
        Result.Failure<LoginResponse>(Error.Validation("Auth.InvalidRefreshToken", "Invalid or expired refresh token."));

    /// <summary>
    /// R2-DEV-03A — the presented refresh token is not the CURRENT token of any live session of
    /// this user. Classify it against consumed-token history:
    /// <list type="bullet">
    /// <item>Not in history, or in history for a different user, or presented with an access
    /// token whose JTI is not the one it was paired with → plain rejection; NOTHING is revoked.
    /// A known digest alone can therefore never be used to revoke someone's session.</item>
    /// <item>Same signed pair within DuplicateWindowSeconds of its consumption → DUPLICATE
    /// (e.g. a client retry after a lost response, or a second tab): rejected, nothing
    /// revoked, no credentials issued.</item>
    /// <item>Same signed pair after the window → REUSE: only that one session family
    /// (UserSession.Id) is revoked. Other devices and tenants are untouched.</item>
    /// </list>
    /// The window cannot tell an attacker from a slow legitimate retry: a genuine retry that
    /// arrives after it is treated as reuse. That is a policy trade-off, not proof of intent.
    /// Every outcome returns the same generic error.
    /// </summary>
    private async Task<Result<LoginResponse>> RejectUnmatchedRefreshTokenAsync(
        Guid userId, string? accessTokenJti, string presentedHash, DateTime now)
    {
        var consumed = await _sessions.FindConsumedByHashAsync(presentedHash);

        if (consumed is null
            || consumed.UserId != userId
            || string.IsNullOrEmpty(accessTokenJti)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(consumed.AccessTokenJti),
                System.Text.Encoding.UTF8.GetBytes(accessTokenJti)))
        {
            return InvalidRefreshToken();
        }

        var sinceConsumed = now - consumed.ConsumedAt;
        if (sinceConsumed <= TimeSpan.FromSeconds(_refreshTokenSettings.DuplicateWindowSeconds))
        {
            _logger.LogWarning(
                "Refresh for user {UserId}: duplicate of a token consumed {Seconds:F1}s ago on session {SessionId}; rejected, session not revoked",
                userId, sinceConsumed.TotalSeconds, consumed.SessionId);
            return InvalidRefreshToken();
        }

        var family = await _sessions.GetByIdAsync(consumed.SessionId);
        if (family is not null && family.UserId == userId && !family.IsRevoked)
        {
            await RevokeSessionAsync(family, "RefreshTokenReuse", now);
            _logger.LogWarning(
                "Refresh for user {UserId}: REUSE of a token consumed {Seconds:F0}s ago (version {Version}); session family {SessionId} revoked",
                userId, sinceConsumed.TotalSeconds, consumed.RotationVersion, consumed.SessionId);
        }

        return InvalidRefreshToken();
    }

    /// <summary>
    /// Revokes one session so that the revocation ALWAYS wins. Bumping RotationVersion makes any
    /// concurrent rotation of the same row fail; if this write itself conflicts (the row moved
    /// on), the row is re-read and the revocation re-applied to its current state. Already
    /// revoked → no-op (the original reason is kept).
    /// </summary>
    private async Task RevokeSessionAsync(UserSession session, string reason, DateTime now)
        => await SaveRevokingSessionsAsync([session], reason, now);

    /// <summary>
    /// Marks every not-yet-revoked session in <paramref name="sessions"/> revoked (+1 version) and
    /// saves ALL pending changes in the unit of work (e.g. a password change) in one SaveChanges.
    /// On an optimistic-concurrency conflict the sessions are re-read and the revocation is
    /// re-applied, up to <see cref="MaxRevocationAttempts"/> times; other pending changes are kept.
    /// A refresh can therefore never restore a session this call revoked.
    /// </summary>
    private async Task SaveRevokingSessionsAsync(IReadOnlyCollection<UserSession> sessions, string reason, DateTime now)
    {
        for (var attempt = 1; ; attempt++)
        {
            foreach (var session in sessions.Where(s => !s.IsRevoked))
            {
                session.IsRevoked = true;
                session.RevokedAt = now;
                session.RevokedReason = reason;
                session.RotationVersion++;
            }

            try
            {
                await _unitOfWork.SaveChangesAsync();
                return;
            }
            catch (Exception ex) when (IsConcurrencyConflict(ex) && attempt < MaxRevocationAttempts)
            {
                _logger.LogInformation(
                    "Revocation ({Reason}) hit a concurrent session update; re-reading and re-applying (attempt {Attempt})",
                    reason, attempt);
                await _sessions.ReloadAsync(sessions);
            }
        }
    }

    /// <summary>
    /// True for EF Core's optimistic-concurrency failure (an UPDATE/DELETE guarded by a
    /// concurrency token affected no row). Matched by type name so this service keeps no EF
    /// Core dependency (IdentityServices_Should_Not_Depend_On_EfCore architecture test).
    /// </summary>
    private static bool IsConcurrencyConflict(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().FullName == "Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException")
                return true;
        }

        return false;
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
