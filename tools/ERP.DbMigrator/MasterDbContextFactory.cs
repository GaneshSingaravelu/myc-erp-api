using ERP.Identity.Configuration;
using ERP.Persistence.Context;
using ERP.Persistence.PostgreSql.Extensions;
using ERP.Persistence.Providers;
using ERP.Persistence.SqlServer.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ERP.DbMigrator;

/// <summary>
/// DESIGN-TIME FACTORY for MasterDbContext.
///
/// WHY IS THIS NEEDED?
/// ====================
/// EF Core CLI commands (dotnet ef migrations add, dotnet ef database update)
/// run OUTSIDE your application — Program.cs never executes, so DI isn't available.
/// This factory tells EF Core how to create a MasterDbContext at design time.
///
/// PROVIDER SELECTION
/// ==================
/// The engine comes from the deployment-level <c>Database:Provider</c> setting, parsed by
/// the same <see cref="DatabaseProviderResolver"/> the runtime composition root uses. That
/// shared parser is the single authority: design time and runtime cannot disagree about
/// which engine a deployment runs on, and a missing or unsupported value FAILS CLOSED
/// rather than defaulting to SQL Server.
///
/// It deliberately does NOT infer the provider from connection-string syntax, and does NOT
/// read <c>Tenants[0].DbProvider</c> — that is a per-tenant column describing a tenant's
/// business database, and it cannot describe the Master database.
///
/// USAGE (run from solution root):
///   dotnet ef migrations add InitialMaster --context MasterDbContext --project tools/ERP.DbMigrator --output-dir Migrations/MasterDb
///   dotnet ef database update --context MasterDbContext --project tools/ERP.DbMigrator
/// </summary>
public class MasterDbContextFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        // Read configuration from the API host's appsettings, then let environment
        // variables override it.
        //
        // Environment variables come LAST so they win. Without them, selecting the
        // PostgreSQL design-time provider would mean editing the shared host
        // appsettings.json — a tracked file — just to run a `dotnet ef` command. That is
        // not an operationally clean way to drive a dual-provider workflow.
        //
        // This adds no new configuration KEY and no new parser: the standard .NET
        // hierarchical form Database__Provider supplies the same Database:Provider key
        // that DatabaseProviderResolver already owns. For example:
        //
        //   Database__Provider=PostgreSql dotnet ef migrations list \
        //     --context MasterDbContext --project tools/ERP.DbMigrator --no-connect
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindSolutionRoot())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return new MasterDbContext(BuildOptions(configuration));
    }

    /// <summary>
    /// Builds the design-time options for <see cref="MasterDbContext"/> from the supplied
    /// configuration.
    ///
    /// Separated from <see cref="CreateDbContext"/> — which has to locate and read the
    /// host's appsettings from disk — so provider selection can be tested directly against
    /// an in-memory configuration, with no filesystem or database dependency.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the MasterDb connection string is absent, or when
    /// <c>Database:Provider</c> is missing, blank or not a supported provider.
    /// </exception>
    public static DbContextOptions<MasterDbContext> BuildOptions(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MasterDb")
            ?? throw new InvalidOperationException(
                "MasterDb connection string not found. Ensure appsettings.Development.json has ConnectionStrings:MasterDb.");

        // Fails closed on a missing, blank or unsupported value — no fallback provider.
        var provider = DatabaseProviderResolver.Parse(
            configuration[DatabaseProviderResolver.ConfigurationKey]);

        // Register Identity entity configurations (same as runtime — uses the Identity assembly)
        var identityAssembly = typeof(TenantConfiguration).Assembly;
        if (!MasterDbContext.ConfigurationAssemblies.Contains(identityAssembly))
            MasterDbContext.ConfigurationAssemblies.Add(identityAssembly);

        var optionsBuilder = new DbContextOptionsBuilder<MasterDbContext>();

        // The provider seams own the migrations assembly and the rest of the
        // provider-specific configuration. Using them here keeps design time and runtime
        // on one configuration path instead of two that can drift apart.
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
                    $"Unsupported database provider for the Master database: '{provider}'. " +
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
}
