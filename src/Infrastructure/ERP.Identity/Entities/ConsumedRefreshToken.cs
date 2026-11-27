using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// R2-DEV-03A — A refresh token that has been consumed by a successful rotation.
///
/// Written in the SAME SaveChanges (one transaction) as the session update that consumed it,
/// so history and rotation commit or fail together. Used to tell a duplicate (same pair
/// re-sent within the ambiguity window) from reuse (re-sent later), which revokes the family.
///
/// Only the keyed digest is stored. <see cref="AccessTokenJti"/> is the JTI of the access token
/// that was paired with the consumed refresh token: reuse is only acted on when the caller
/// presents that same signed pair, so a known digest alone cannot revoke someone's session.
/// </summary>
public class ConsumedRefreshToken : BaseEntity
{
    /// <summary>The device session family (UserSession.Id) the token belonged to.</summary>
    public Guid SessionId { get; set; }
    public UserSession Session { get; set; } = null!;

    public Guid UserId { get; set; }

    /// <summary>HMAC-SHA-256 hex digest of the consumed refresh token (unique).</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>JTI of the access token issued together with the consumed refresh token.</summary>
    public string AccessTokenJti { get; set; } = string.Empty;

    /// <summary>The session's RotationVersion at the moment this token was consumed.</summary>
    public int RotationVersion { get; set; }

    public DateTime ConsumedAt { get; set; }

    /// <summary>
    /// Earliest time this record may be purged: the session's absolute expiry. Until then the
    /// session can still be live, so the record is needed for reuse detection.
    /// </summary>
    public DateTime RetainUntil { get; set; }
}
