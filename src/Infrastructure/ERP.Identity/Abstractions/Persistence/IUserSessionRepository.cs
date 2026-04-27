using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Repository for UserSession (Master DB).
///
/// Sessions back the multi-device login + revocation model: each successful login
/// creates one row; refresh rotates it; logout / password-change revokes it.
///
/// Persistence is committed via IMasterUnitOfWork.SaveChangesAsync().
/// </summary>
public interface IUserSessionRepository
{
    /// <summary>Stage a new session for insertion.</summary>
    Task AddAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find an active, non-revoked, non-expired session matching (userId, refreshToken).
    /// Used during refresh token flow.
    /// </summary>
    Task<UserSession?> FindActiveByRefreshTokenAsync(
        Guid userId,
        string refreshToken,
        DateTime nowUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a non-revoked session matching (userId, refreshToken) regardless of expiry.
    /// Used by logout — idempotent: returns null if already revoked or never existed.
    /// </summary>
    Task<UserSession?> FindNonRevokedByRefreshTokenAsync(
        Guid userId,
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>Get all active (non-revoked) sessions for a user. Used by password-change to revoke them all.</summary>
    Task<List<UserSession>> GetActiveSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
