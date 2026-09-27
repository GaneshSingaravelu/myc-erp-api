using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Persistence;

/// <summary>
/// EF Core implementation of <see cref="ITenantRepository"/> over MasterDbContext.
/// Covers the read-only lookups used by AuthService and the CRUD surface used by
/// TenantManagementService.
/// </summary>
public sealed class TenantRepository : ITenantRepository
{
    private readonly MasterDbContext _context;

    public TenantRepository(MasterDbContext context) => _context = context;

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<Tenant>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    /// <summary>
    /// Looks up a tenant by code. The supplied value is canonicalized HERE — inside the
    /// repository — so no caller can bypass the invariant by passing a raw value. Stored
    /// codes are canonical too, so the comparison never depends on database collation.
    /// </summary>
    public Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = TenantCode.Normalize(code);
        return _context.Set<Tenant>().FirstOrDefaultAsync(t => t.Code == normalized, cancellationToken);
    }

    public Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<Tenant>().AnyAsync(t => t.Id == id, cancellationToken);

    /// <summary>
    /// Existence check by code, canonicalized on the same terms as <see cref="GetByCodeAsync"/>.
    /// This is what makes the duplicate-code check case-insensitive: an incoming <c>ACME</c>
    /// is compared as <c>acme</c> and therefore collides with a stored <c>acme</c>.
    /// </summary>
    public Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = TenantCode.Normalize(code);
        return _context.Set<Tenant>().AnyAsync(t => t.Code == normalized, cancellationToken);
    }

    public Task<Tenant?> GetByIdWithUserTenantsAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<Tenant>()
            .Include(t => t.UserTenants)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var tenants = await _context.Set<Tenant>()
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return tenants;
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken = default)
        => await _context.Set<Tenant>().AddAsync(tenant, cancellationToken);
}
