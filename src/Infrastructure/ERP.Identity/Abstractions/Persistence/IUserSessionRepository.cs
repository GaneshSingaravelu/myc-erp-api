using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Repository for UserSession (Master DB).
///
/// Sessions back the multi-device login + revocation model: each successful login
/// creates one row; refresh rotates it; logout / password-change revokes it.
///
/// Persistence is committed via IMasterUnitOfWork.SaveChangesAsync().
///
/// R2-DEV-03A: refresh tokens are looked up by their keyed digest (never plaintext), consumed
/// tokens are kept in <see cref="ConsumedRefreshToken"/> history, and every session write is
/// guarded by the RotationVersion concurrency token.
/// </summary>
public interface IUserSessionRepository
{
    /// <summary>Stage a new session for insertion.</summary>
    Task AddAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find an active, non-revoked, non-expired session whose CURRENT refresh-token digest is
    /// <paramref name="refreshTokenHash"/> for <paramref name="userId"/>. Used during refresh.
    /// </summary>
    Task<UserSession?> FindActiveByRefreshTokenHashAsync(
        Guid userId,
        string refreshTokenHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a non-revoked session by (userId, current refresh-token digest) regardless of expiry.
    /// Used by logout — idempotent: returns null if already revoked or never existed.
    /// </summary>
    Task<UserSession?> FindNonRevokedByRefreshTokenHashAsync(
        Guid userId,
        string refreshTokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>Get a session by its Id (the device session family).</summary>
    Task<UserSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Get all active (non-revoked) sessions for a user. Used by password-change to revoke them all.</summary>
    Task<List<UserSession>> GetActiveSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-read tracked sessions from the database, discarding any pending changes to them.
    /// Used after an optimistic-concurrency conflict before re-applying a revocation.
    /// </summary>
    Task ReloadAsync(IEnumerable<UserSession> sessions, CancellationToken cancellationToken = default);

    /// <summary>Stage a consumed-token history record (committed with the rotation).</summary>
    Task AddConsumedAsync(ConsumedRefreshToken consumed, CancellationToken cancellationToken = default);

    /// <summary>Find consumed-token history by digest (no tracking).</summary>
    Task<ConsumedRefreshToken?> FindConsumedByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when an access token's JTI belongs to a live session of <paramref name="userId"/>.
    /// Read fresh from the database on every call (no cache), so a revocation on any instance
    /// is visible to every instance at once.
    /// </summary>
    Task<bool> IsAccessTokenSessionActiveAsync(Guid userId, string jti, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bounded maintenance: delete at most <paramref name="batchSize"/> consumed-token records whose
    /// <see cref="ConsumedRefreshToken.RetainUntil"/> has passed (oldest first). Records of a live
    /// session always have RetainUntil in the future (it is the session's absolute expiry), so
    /// they are never deleted. With <paramref name="dryRun"/> it only counts. Relational providers only.
    /// </summary>
    Task<int> PurgeExpiredConsumedAsync(DateTime nowUtc, int batchSize, bool dryRun, CancellationToken cancellationToken = default);
}
