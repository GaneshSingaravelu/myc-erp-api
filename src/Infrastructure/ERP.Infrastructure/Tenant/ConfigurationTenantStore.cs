using Microsoft.Extensions.Configuration;

namespace ERP.Infrastructure.Tenant;

/// <summary>
/// CONFIGURATION-BASED TENANT STORE — Reads tenants from appsettings.json.
///
/// WHY START WITH CONFIGURATION?
/// ==============================
/// For development and small deployments, storing tenant info in appsettings.json
/// is simple and works immediately. No master database needed.
///
/// Later, we'll add an EfCoreTenantStore that reads from a real master database.
/// Because TenantMiddleware depends on ITenantStore (interface), we can swap
/// implementations without changing ANY middleware code.
///
/// CONFIGURATION FORMAT (appsettings.json):
/// =========================================
/// {
///   "Tenants": [
///     {
///       "Id": "...",
///       "Identifier": "abc-manufacturing",
///       "Name": "ABC Manufacturing Pvt Ltd",
///       "ConnectionString": "Server=localhost;Database=ERP_ABC;...",
///       "DbProvider": "SqlServer",
///       "IsActive": true
///     },
///     {
///       "Id": "...",
///       "Identifier": "xyz-corp",
///       "Name": "XYZ Corporation",
///       "ConnectionString": "Host=localhost;Database=ERP_XYZ;...",
///       "DbProvider": "PostgreSql",
///       "IsActive": true
///     }
///   ]
/// }
///
/// DESIGN PATTERN: Strategy Pattern (one of many possible ITenantStore implementations)
/// </summary>
public class ConfigurationTenantStore : ITenantStore
{
    private readonly List<TenantInfo> _tenants;

    public ConfigurationTenantStore(IConfiguration configuration)
    {
        _tenants = configuration.GetSection("Tenants").Get<List<TenantInfo>>() ?? [];
    }

    public Task<TenantInfo?> GetByIdentifierAsync(string identifier)
    {
        var tenant = _tenants.FirstOrDefault(t =>
            t.Identifier.Equals(identifier, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(tenant);
    }

    public Task<IReadOnlyList<TenantInfo>> GetAllAsync()
    {
        return Task.FromResult<IReadOnlyList<TenantInfo>>(_tenants.AsReadOnly());
    }
}
