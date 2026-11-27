using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// USER SESSION — Tracks active login sessions. Supports multi-device, token revocation.
///
/// Replaces the old single RefreshToken column on AppUser.
///
/// WHY A SEPARATE TABLE?
/// ======================
/// - Multi-device: user can be logged in on phone + laptop + tablet simultaneously
/// - Token revocation: admin can revoke a specific session (stolen device)
/// - Session tracking: IP, device info, last access time
/// - AccessTokenJti: enables token blacklisting without waiting for expiry
///
/// TOKEN REFRESH FLOW:
/// ====================
/// 1. Client sends expired access token + refresh token
/// 2. Server finds session by refresh token
/// 3. Validates: not revoked, not expired, user active
/// 4. Issues new access token + new refresh token (rotation)
/// 5. Updates LastAccessedAt, replaces RefreshTokenHash + AccessTokenJti
///
/// R2-DEV-03A — VERSIONED DEVICE SESSIONS:
/// - The session Id is the device session FAMILY: every rotation keeps the same row.
/// - Only an HMAC-SHA-256 digest of the current refresh token is stored (never plaintext).
/// - <see cref="RotationVersion"/> is an EF concurrency token and is incremented by EVERY state
///   change (rotation and revocation). Each write is therefore
///   <c>UPDATE … WHERE Id = @id AND RotationVersion = @original</c>: a stale writer affects no
///   row and fails, so it can neither overwrite a newer rotation nor un-revoke a session.
/// </summary>
public class UserSession : BaseEntity
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    /// <summary>Which tenant context this session was created in</summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>HMAC-SHA-256 hex digest of the current refresh token (unique). Plaintext is never stored.</summary>
    public string RefreshTokenHash { get; set; } = string.Empty;

    /// <summary>Optimistic-concurrency version; +1 on every rotation and revocation.</summary>
    public int RotationVersion { get; set; }

    /// <summary>Tokens consumed by earlier rotations of this session (reuse detection).</summary>
    public ICollection<ConsumedRefreshToken> ConsumedTokens { get; set; } = [];

    /// <summary>When the refresh token expires</summary>
    public DateTime RefreshTokenExpiry { get; set; }

    /// <summary>JWT unique ID (jti claim) — enables token blacklisting</summary>
    public string AccessTokenJti { get; set; } = string.Empty;

    /// <summary>User-Agent / device description (e.g., "Chrome 120 on Windows 11")</summary>
    public string? DeviceInfo { get; set; }

    /// <summary>Client IP address at login</summary>
    public string? IpAddress { get; set; }

    /// <summary>Geo-location derived from IP (optional, future use)</summary>
    public string? Location { get; set; }

    /// <summary>Has this session been manually or automatically revoked?</summary>
    public bool IsRevoked { get; set; }

    /// <summary>When the session was revoked</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>"Logout", "PasswordChanged", "AdminRevoked", "Expired", "SecurityStampChanged"</summary>
    public string? RevokedReason { get; set; }

    /// <summary>Absolute session expiry (independent of refresh token)</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the session was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Updated on each token refresh</summary>
    public DateTime? LastAccessedAt { get; set; }
}
