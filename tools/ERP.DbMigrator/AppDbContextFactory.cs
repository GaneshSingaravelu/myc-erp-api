using ERP.Persistence.Context;
using ERP.Persistence.PostgreSql.Extensions;
using ERP.Persistence.Providers;
using ERP.Persistence.SqlServer.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ERP.DbMigrator;

/// <summary>
/// DESIGN-TIME FACTORY for AppDbContext (Tenant databases).
///
/// WHY IS THIS NEEDED?
/// ====================
/// EF Core CLI commands run outside the application — no DI, no TenantMiddleware,
/// no TenantDbContextFactory. This factory creates an AppDbContext for migration generation.
///
/// PROVIDER SELECTION
/// ==================
/// The engine comes from the deployment-level <c>Database:Provider</c> setting, parsed by
/// the same <see cref="DatabaseProviderResolver"/> the runtime composition root and the
/// Master design-time factory use. A missing or unsupported value FAILS CLOSED.
///
/// It used to come from <c>Tenants[0].DbProvider</c> — whichever tenant happened to be
/// first in the configuration array. That is why PostgreSQL tenant migrations were never
/// generated: the first configured tenant uses SQL Server, so switching engine meant
/// reordering a config array. A deployment runs one engine for its Master database and all
/// its tenant databases, so the deployment-level setting is the correct authority here.
/// <c>Tenant.DbProvider</c> keeps its own job — it is the RUNTIME per-tenant value read by
/// <c>TenantDbContextFactory</c>, and is unaffected by this.
///
/// The tenant CONNECTION STRING still comes from the first configured tenant entry. Tenant
/// selection for migration generation is deliberately unchanged here; only the provider
/// authority moved.
///
/// USAGE (run from solution root):
///   dotnet ef migrations add InitialTenant --context AppDbContext --project tools/ERP.DbMigrator --output-dir Migrations/AppDb
///
///   Database__Provider=PostgreSql dotnet ef migrations add InitialTenant \
///     --context AppDbContext --project tools/ERP.DbMigrator --output-dir Migrations/AppDb
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Read configuration from the API host's appsettings, then let environment
        // variables override it. Environment variables come LAST so they win, which is how
        // a `dotnet ef` invocation selects the other provider without editing the shared,
        // tracked host appsettings.json. Same layering as the Master design-time factory.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindSolutionRoot())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return new AppDbContext(BuildOptions(configuration));
    }

    /// <summary>
    /// Builds the design-time options for <see cref="AppDbContext"/> from the supplied
    /// configuration.
    ///
    /// Separated from <see cref="CreateDbContext"/> — which has to locate and read the
    /// host's appsettings from disk — so provider selection can be tested directly against
    /// an in-memory configuration, with no filesystem or database dependency.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no tenant is configured, when the first tenant's connection string is
    /// absent, or when <c>Database:Provider</c> is missing, blank or not a supported provider.
    /// </exception>
    public static DbContextOptions<AppDbContext> BuildOptions(IConfiguration configuration)
    {
        // Use the first configured tenant's connection string for migration generation.
        // Generated migrations apply to ALL tenant databases — they share one schema — so
        // which tenant supplies the connection string does not affect the output.
        var tenantSection = configuration.GetSection("Tenants").GetChildren().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No tenants configured. Ensure appsettings.Development.json has at least one tenant in the Tenants array.");

        var connectionString = tenantSection["ConnectionString"]
            ?? throw new InvalidOperationException("First tenant's ConnectionString is missing.");

        // Provider comes from the deployment setting, NOT from tenantSection["DbProvider"].
        // Fails closed on a missing, blank or unsupported value — no fallback provider.
        var provider = DatabaseProviderResolver.Parse(
            configuration[DatabaseProviderResolver.ConfigurationKey]);

        // Register module entity configurations (same as runtime)
        RegisterModuleConfigurations();

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        // The provider seams own the migrations assembly and the rest of the
        // provider-specific configuration, so design time and runtime stay on one
        // configuration path instead of two that can drift apart.
        switch (provider)
        {
            case DatabaseProvider.SqlServer:
                optionsBuilder.UseSqlServerProvider(connectionString);
                break;

            case DatabaseProvider.PostgreSql:
                optionsBuilder.UsePostgreSqlProvider(connectionString);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider for tenant databases: '{provider}'. " +
                    $"Supported providers: {DatabaseProviderResolver.SupportedProviders}");
        }

        return optionsBuilder.Options;
    }

    /// <summary>
    /// Walks up from the current directory to find the solution root (where ERP.slnx lives),
    /// then returns the path to the API host's appsettings directory.
    /// </summary>
    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ERP.slnx")))
            dir = dir.Parent;

        if (dir == null)
            throw new InvalidOperationException(
                "Could not find solution root (ERP.slnx). Run this command from the solution directory or a subdirectory.");

        return Path.Combine(dir.FullName, "src", "Host", "ERP.Api");
    }

    /// <summary>
    /// Registers the same module configuration assemblies that runtime does.
    /// Each module's ServiceExtensions normally calls AppDbContext.ConfigurationAssemblies.Add()
    /// during DI registration. At design time, we do it manually.
    /// </summary>
    private static void RegisterModuleConfigurations()
    {
        var workflowAssembly = typeof(ERP.Workflow.Infrastructure.Extensions.WorkflowServiceExtensions).Assembly;
        if (!AppDbContext.ConfigurationAssemblies.Contains(workflowAssembly))
            AppDbContext.ConfigurationAssemblies.Add(workflowAssembly);

        var productionAssembly = typeof(ERP.Production.Infrastructure.Extensions.ProductionServiceExtensions).Assembly;
        if (!AppDbContext.ConfigurationAssemblies.Contains(productionAssembly))
            AppDbContext.ConfigurationAssemblies.Add(productionAssembly);
    }
}
