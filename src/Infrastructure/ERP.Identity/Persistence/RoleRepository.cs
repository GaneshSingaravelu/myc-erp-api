using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IRoleRepository"/> over MasterDbContext.
/// Encapsulates the RolePermissions → Permission Include chain so RoleService
/// stays free of EF Core references.
/// </summary>
public sealed class RoleRepository : IRoleRepository
{
    private readonly MasterDbContext _context;

    public RoleRepository(MasterDbContext context) => _context = context;

    public Task<AppRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppRole>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<AppRole?> GetByIdWithPermissionsAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppRole>()
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<AppRole?> GetByIdForPermissionAssignmentAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppRole>()
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<AppRole>> GetAllWithPermissionsAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _context.Set<AppRole>()
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return roles;
    }

    public Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppRole>().AnyAsync(r => r.Id == id, cancellationToken);

    public Task<bool> ExistsAndIsAssignableInTenantAsync(
        Guid roleId, Guid tenantId, CancellationToken cancellationToken = default)
        => _context.Set<AppRole>()
            .AnyAsync(r => r.Id == roleId && (r.TenantId == null || r.TenantId == tenantId),
                cancellationToken);

    public Task<bool> ExistsByNameAsync(string name, CancellationToken cancellationToken = default)
        => _context.Set<AppRole>().AnyAsync(r => r.Name == name, cancellationToken);

    public async Task AddAsync(AppRole role, CancellationToken cancellationToken = default)
        => await _context.Set<AppRole>().AddAsync(role, cancellationToken);

    public void Remove(AppRole role) => _context.Set<AppRole>().Remove(role);

    public void RemoveRolePermissions(IEnumerable<AppRolePermission> rolePermissions)
        => _context.Set<AppRolePermission>().RemoveRange(rolePermissions);
}
