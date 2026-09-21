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

    public async Task<IReadOnlyList<AppRole>> GetAllVisibleInTenantWithPermissionsAsync(
        Guid tenantId, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: this is a pure projection path — RoleService maps straight to a DTO
        // and never writes the graph back. Keeping it out of the change tracker means the
        // rows returned here are the rows the database matched, and cannot be substituted by
        // an instance some earlier operation in the same request already tracked. The
        // read-modify-save callers deliberately keep using the tracked overloads above.
        var roles = await _context.Set<AppRole>()
            .AsNoTracking()
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .Where(r => r.TenantId == null || r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return roles;
    }

    public Task<AppRole?> GetByIdVisibleInTenantWithPermissionsAsync(
        Guid id, Guid tenantId, CancellationToken cancellationToken = default)
        // The tenant predicate sits in the same WHERE as the id, so the database never
        // returns a foreign tenant's role for us to filter out afterwards.
        => _context.Set<AppRole>()
            .AsNoTracking()
            .Include(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(
                r => r.Id == id && (r.TenantId == null || r.TenantId == tenantId),
                cancellationToken);

    public Task<AppRole?> GetTrackedByIdVisibleInTenantAsync(
        Guid id, Guid tenantId, CancellationToken cancellationToken = default)
        // Tracked on purpose — the caller writes to what comes back. No AsNoTracking, and no
        // Include: the permission graph is loaded separately, after authorization.
        => _context.Set<AppRole>()
            .FirstOrDefaultAsync(
                r => r.Id == id && (r.TenantId == null || r.TenantId == tenantId),
                cancellationToken);

    public Task LoadRolePermissionsAsync(AppRole role, CancellationToken cancellationToken = default)
        => _context.Entry(role)
            .Collection(r => r.RolePermissions)
            .Query()
            .Include(rp => rp.Permission)
            .LoadAsync(cancellationToken);

    public Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<AppRole>().AnyAsync(r => r.Id == id, cancellationToken);

    public Task<bool> ExistsAndIsAssignableInTenantAsync(
        Guid roleId, Guid tenantId, CancellationToken cancellationToken = default)
        => _context.Set<AppRole>()
            .AnyAsync(r => r.Id == roleId && (r.TenantId == null || r.TenantId == tenantId),
                cancellationToken);

    public Task<bool> ExistsByNormalizedNameInTenantOrGlobalAsync(
        string normalizedName,
        Guid tenantId,
        Guid? excludeRoleId,
        CancellationToken cancellationToken = default)
        // Every arm is part of one database predicate — nothing is fetched and compared here.
        => _context.Set<AppRole>()
            .AnyAsync(
                r => r.NormalizedName == normalizedName
                     && (r.TenantId == null || r.TenantId == tenantId)
                     && (excludeRoleId == null || r.Id != excludeRoleId),
                cancellationToken);

    public async Task AddAsync(AppRole role, CancellationToken cancellationToken = default)
        => await _context.Set<AppRole>().AddAsync(role, cancellationToken);

    public void Remove(AppRole role) => _context.Set<AppRole>().Remove(role);

    public void RemoveRolePermissions(IEnumerable<AppRolePermission> rolePermissions)
        => _context.Set<AppRolePermission>().RemoveRange(rolePermissions);
}
