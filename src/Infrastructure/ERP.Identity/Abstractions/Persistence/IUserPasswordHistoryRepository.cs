using ERP.Identity.Entities;

namespace ERP.Identity.Abstractions.Persistence;

/// <summary>
/// Repository for UserPasswordHistory (Master DB).
///
/// Tracks the most recent N password hashes per user to enforce the no-reuse policy.
/// </summary>
public interface IUserPasswordHistoryRepository
{
    /// <summary>Get the most recent <paramref name="count"/> password hashes for a user, newest first.</summary>
    Task<List<UserPasswordHistory>> GetRecentForUserAsync(
        Guid userId,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>Stage a new password-history entry for insertion.</summary>
    Task AddAsync(UserPasswordHistory entry, CancellationToken cancellationToken = default);
}
