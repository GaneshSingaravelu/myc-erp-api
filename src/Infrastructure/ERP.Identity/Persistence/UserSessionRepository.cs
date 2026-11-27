using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IUserSessionRepository"/> over MasterDbContext.
/// </summary>
public sealed class UserSessionRepository : IUserSessionRepository
{
    private readonly MasterDbContext _context;

    public UserSessionRepository(MasterDbContext context) => _context = context;

    public async Task AddAsync(UserSession session, CancellationToken cancellationToken = default)
        => await _context.Set<UserSession>().AddAsync(session, cancellationToken);

    public Task<UserSession?> FindActiveByRefreshTokenHashAsync(
        Guid userId,
        string refreshTokenHash,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
        => _context.Set<UserSession>()
            .FirstOrDefaultAsync(s => s.UserId == userId
                && s.RefreshTokenHash == refreshTokenHash
                && !s.IsRevoked
                && s.RefreshTokenExpiry > nowUtc
                && s.ExpiresAt > nowUtc, cancellationToken);

    public Task<UserSession?> FindNonRevokedByRefreshTokenHashAsync(
        Guid userId,
        string refreshTokenHash,
        CancellationToken cancellationToken = default)
        => _context.Set<UserSession>()
            .FirstOrDefaultAsync(s => s.UserId == userId
                && s.RefreshTokenHash == refreshTokenHash
                && !s.IsRevoked, cancellationToken);

    public Task<UserSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _context.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

    public Task<List<UserSession>> GetActiveSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => _context.Set<UserSession>()
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .ToListAsync(cancellationToken);

    public async Task ReloadAsync(IEnumerable<UserSession> sessions, CancellationToken cancellationToken = default)
    {
        foreach (var session in sessions)
            await _context.Entry(session).ReloadAsync(cancellationToken);
    }

    public async Task AddConsumedAsync(ConsumedRefreshToken consumed, CancellationToken cancellationToken = default)
        => await _context.Set<ConsumedRefreshToken>().AddAsync(consumed, cancellationToken);

    public Task<ConsumedRefreshToken?> FindConsumedByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        => _context.Set<ConsumedRefreshToken>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TokenHash == tokenHash, cancellationToken);

    public Task<bool> IsAccessTokenSessionActiveAsync(Guid userId, string jti, DateTime nowUtc, CancellationToken cancellationToken = default)
        => _context.Set<UserSession>()
            .AsNoTracking()
            .AnyAsync(s => s.UserId == userId
                && s.AccessTokenJti == jti
                && !s.IsRevoked
                && s.ExpiresAt > nowUtc, cancellationToken);

    public async Task<int> PurgeExpiredConsumedAsync(DateTime nowUtc, int batchSize, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be at least 1.");

        // Oldest first, bounded: at most batchSize rows are read and deleted per call, so a
        // run holds locks only for that batch. Run repeatedly to drain a backlog.
        var expired = _context.Set<ConsumedRefreshToken>()
            .Where(c => c.RetainUntil < nowUtc)
            .OrderBy(c => c.RetainUntil)
            .Take(batchSize);

        if (dryRun)
            return await expired.CountAsync(cancellationToken);

        var ids = await expired.Select(c => c.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0)
            return 0;

        return await _context.Set<ConsumedRefreshToken>()
            .Where(c => ids.Contains(c.Id) && c.RetainUntil < nowUtc) // re-checked at delete time
            .ExecuteDeleteAsync(cancellationToken);
    }
}
