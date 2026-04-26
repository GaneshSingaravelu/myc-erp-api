using ERP.Application.Abstractions.MultiTenancy;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using ERP.Persistence.SqlServer.Extensions;
using ERP.Persistence.PostgreSql.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP.Infrastructure.Tenant;

/// <summary>
/// TENANT DB CONTEXT FACTORY — Creates the correct database connection per tenant.
///
/// THIS IS THE HEART OF MULTI-TENANCY + MULTI-PROVIDER:
/// =====================================================
/// This factory reads the current tenant's configuration and creates an AppDbContext
/// connected to the RIGHT database using the RIGHT provider.
///
/// FLOW:
///   HTTP Request arrives
///     → TenantMiddleware sets CurrentTenant (from header)
///     → Controller needs data → DI creates Repository → Repository needs AppDbContext
///     → DI calls TenantDbContextFactory.CreateDbContext()
///     → Factory reads CurrentTenant.DbProvider:
///         "SqlServer"  → options.UseSqlServer(tenant.ConnectionString)
///         "PostgreSql" → options.UseNpgsql(tenant.ConnectionString)
///     → Returns AppDbContext connected to correct tenant database
///     → Repository uses it normally (doesn't know which DB it's talking to!)
///
/// WHAT IS IDbContextFactory? (Interview Question!)
/// ==================================================
/// IDbContextFactory&lt;T&gt; is a factory pattern from EF Core.
/// Instead of letting DI create DbContext directly, the factory creates it.
/// This gives us control over HOW the DbContext is configured (connection string, provider).
///
/// WHY A FACTORY INSTEAD OF DIRECT DI?
/// =====================================
/// With direct DI, the connection string is fixed at startup:
///   services.AddDbContext&lt;AppDbContext&gt;(o => o.UseSqlServer("fixed-connection"));
///
/// With a factory, we can change the connection PER REQUEST:
///   services.AddScoped&lt;AppDbContext&gt;(sp => sp.GetRequired...Factory().CreateDbContext());
///
/// This is essential for multi-tenancy where each tenant has a different database.
///
/// DESIGN PATTERNS: Factory Pattern + Strategy Pattern
/// </summary>
public class TenantDbContextFactory : IDbContextFactory<AppDbContext>
{
    private readonly ITenantProvider _tenantProvider;
    private readonly AuditableEntityInterceptor _auditInterceptor;
    private readonly DomainEventDispatcherInterceptor _domainEventInterceptor;
    private readonly ILogger<TenantDbContextFactory> _logger;

    public TenantDbContextFactory(
        ITenantProvider tenantProvider,
        AuditableEntityInterceptor auditInterceptor,
        DomainEventDispatcherInterceptor domainEventInterceptor,
        ILogger<TenantDbContextFactory> logger)
    {
        _tenantProvider = tenantProvider;
        _auditInterceptor = auditInterceptor;
        _domainEventInterceptor = domainEventInterceptor;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new AppDbContext connected to the current tenant's database.
    /// Called automatically by DI when a service needs AppDbContext.
    /// </summary>
    public AppDbContext CreateDbContext()
    {
        var tenant = _tenantProvider.GetCurrentTenant();

        _logger.LogDebug("Creating DbContext for tenant {TenantName} using {Provider}",
            tenant.Name, tenant.DbProvider);

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        // Wire interceptors — ensures audit fields and domain events work for tenant contexts
        optionsBuilder.AddInterceptors(_auditInterceptor, _domainEventInterceptor);

        // Switch database provider based on tenant configuration
        switch (tenant.DbProvider.ToLower())
        {
            case "sqlserver":
                optionsBuilder.UseSqlServerProvider(tenant.ConnectionString);
                break;

            case "postgresql":
                optionsBuilder.UsePostgreSqlProvider(tenant.ConnectionString);
                break;

            // Oracle can be added here when the Oracle provider project is created
            // case "oracle":
            //     optionsBuilder.UseOracleProvider(tenant.ConnectionString);
            //     break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider: '{tenant.DbProvider}' for tenant '{tenant.Identifier}'. " +
                    $"Supported providers: SqlServer, PostgreSql");
        }

        return new AppDbContext(optionsBuilder.Options);
    }
}
