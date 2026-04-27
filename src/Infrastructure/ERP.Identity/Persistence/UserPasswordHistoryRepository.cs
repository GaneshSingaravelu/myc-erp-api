using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IUserPasswordHistoryRepository"/> over MasterDbContext.
/// </summary>
public sealed class UserPasswordHistoryRepository : IUserPasswordHistoryRepository
{
    private readonly MasterDbContext _context;

    public UserPasswordHistoryRepository(MasterDbContext context) => _context = context;

    public Task<List<UserPasswordHistory>> GetRecentForUserAsync(
        Guid userId,
        int count,
        CancellationToken cancellationToken = default)
        => _context.Set<UserPasswordHistory>()
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.ChangedAt)
            .Take(count)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(UserPasswordHistory entry, CancellationToken cancellationToken = default)
        => await _context.Set<UserPasswordHistory>().AddAsync(entry, cancellationToken);
}
