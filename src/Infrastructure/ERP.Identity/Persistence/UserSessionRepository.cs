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

    public Task<UserSession?> FindActiveByRefreshTokenAsync(
        Guid userId,
        string refreshToken,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
        => _context.Set<UserSession>()
            .FirstOrDefaultAsync(s => s.UserId == userId
                && s.RefreshToken == refreshToken
                && !s.IsRevoked
                && s.RefreshTokenExpiry > nowUtc
                && s.ExpiresAt > nowUtc, cancellationToken);

    public Task<UserSession?> FindNonRevokedByRefreshTokenAsync(
        Guid userId,
        string refreshToken,
        CancellationToken cancellationToken = default)
        => _context.Set<UserSession>()
            .FirstOrDefaultAsync(s => s.UserId == userId
                && s.RefreshToken == refreshToken
                && !s.IsRevoked, cancellationToken);

    public Task<List<UserSession>> GetActiveSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => _context.Set<UserSession>()
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .ToListAsync(cancellationToken);
}
