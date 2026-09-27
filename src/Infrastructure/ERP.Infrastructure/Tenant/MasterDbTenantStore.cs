using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Tenant;

/// <summary>
/// DATABASE-BACKED TENANT STORE — Reads tenant info from MasterDbContext.
///
/// REPLACES ConfigurationTenantStore:
/// ====================================
/// ConfigurationTenantStore read tenants from appsettings.json (static, dev-only).
/// This implementation reads from the Tenants table in the Master database,
/// allowing dynamic tenant creation via the Admin API.
///
/// WHY NOT SINGLETON?
/// ===================
/// Unlike ConfigurationTenantStore (Singleton, config never changes at runtime),
/// this must be Scoped because MasterDbContext is Scoped (one per request).
/// TenantMiddleware already caches results (30-min TTL) so the DB hit is minimal.
///
/// DESIGN PATTERN: Strategy Pattern — swapped via DI registration, no consumer changes needed.
/// </summary>
public class MasterDbTenantStore : ITenantStore
{
    private readonly MasterDbContext _context;

    public MasterDbTenantStore(MasterDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Resolves a tenant by its identifier (the tenant code from <c>X-Tenant-Id</c>).
    ///
    /// The identifier is canonicalized before the comparison so resolution does not depend
    /// on the database's collation — SQL Server's default is case-insensitive, PostgreSQL's
    /// is not, and a tenant must be the same tenant on both.
    /// </summary>
    public async Task<TenantInfo?> GetByIdentifierAsync(string identifier)
    {
        var normalized = TenantCode.Normalize(identifier);

        var tenant = await _context.Set<Identity.Entities.Tenant>()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Code == normalized);

        return tenant is null ? null : MapToTenantInfo(tenant);
    }

    public async Task<IReadOnlyList<TenantInfo>> GetAllAsync()
    {
        var tenants = await _context.Set<Identity.Entities.Tenant>()
            .AsNoTracking()
            .OrderBy(t => t.Name)
            .ToListAsync();

        return tenants.Select(MapToTenantInfo).ToList().AsReadOnly();
    }

    private static TenantInfo MapToTenantInfo(Identity.Entities.Tenant tenant) => new()
    {
        Id = tenant.Id,
        Identifier = tenant.Code,
        Name = tenant.Name,
        ConnectionString = tenant.ConnectionString,
        DbProvider = tenant.DbProvider,
        IsActive = tenant.IsActive
    };
}
