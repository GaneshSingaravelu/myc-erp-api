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

    public Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
        => _context.Set<Tenant>().FirstOrDefaultAsync(t => t.Code == code, cancellationToken);

    public Task<bool> ExistsByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Set<Tenant>().AnyAsync(t => t.Id == id, cancellationToken);

    public Task<bool> ExistsByCodeAsync(string code, CancellationToken cancellationToken = default)
        => _context.Set<Tenant>().AnyAsync(t => t.Code == code, cancellationToken);

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
