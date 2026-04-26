using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Persistence;

/// <summary>EF Core implementation of <see cref="IPermissionRepository"/> over MasterDbContext.</summary>
public sealed class PermissionRepository : IPermissionRepository
{
    private readonly MasterDbContext _context;

    public PermissionRepository(MasterDbContext context) => _context = context;

    public async Task<IReadOnlyList<Permission>> GetAllOrderedAsync(CancellationToken cancellationToken = default)
    {
        var permissions = await _context.Set<Permission>()
            .OrderBy(p => p.Category)
            .ThenBy(p => p.Code)
            .ToListAsync(cancellationToken);

        return permissions;
    }

    public Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<Permission>().AnyAsync(p => p.Id == id, cancellationToken);
}
