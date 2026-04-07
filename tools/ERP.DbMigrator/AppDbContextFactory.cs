using ERP.Persistence.Context;
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
/// At design time, we use the first configured tenant's connection string.
/// The generated migrations apply to ALL tenant databases (same schema).
///
/// USAGE (SQL Server tenant):
///   dotnet ef migrations add InitialTenant --context AppDbContext --project tools/ERP.DbMigrator --output-dir Migrations/SqlServer
///
/// USAGE (PostgreSQL tenant):
///   Change the connection string below or use a CLI argument.
///   dotnet ef migrations add InitialTenant --context AppDbContext --project tools/ERP.DbMigrator --output-dir Migrations/PostgreSql
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Read configuration from the API host's appsettings
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindSolutionRoot())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        // Use the first tenant's connection string for migration generation
        var tenantSection = configuration.GetSection("Tenants").GetChildren().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No tenants configured. Ensure appsettings.Development.json has at least one tenant in the Tenants array.");

        var connectionString = tenantSection["ConnectionString"]
            ?? throw new InvalidOperationException("First tenant's ConnectionString is missing.");

        var dbProvider = tenantSection["DbProvider"] ?? "SqlServer";

        // Register module entity configurations (same as runtime)
        RegisterModuleConfigurations();

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        if (dbProvider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsAssembly(
                    typeof(ERP.Persistence.PostgreSql.Extensions.PostgreSqlServiceExtensions).Assembly.FullName);
            });
        }
        else
        {
            optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.MigrationsAssembly(
                    typeof(ERP.Persistence.SqlServer.Extensions.SqlServerServiceExtensions).Assembly.FullName);
            });
        }

        return new AppDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// Registers the same module configuration assemblies that runtime does.
    /// Each module's ServiceExtensions normally calls AppDbContext.ConfigurationAssemblies.Add()
    /// during DI registration. At design time, we do it manually.
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
