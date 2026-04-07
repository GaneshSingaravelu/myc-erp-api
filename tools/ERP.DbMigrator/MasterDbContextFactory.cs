using ERP.Identity.Configuration;
using ERP.Persistence.Context;
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
/// USAGE (run from solution root):
///   dotnet ef migrations add InitialMaster --context MasterDbContext --project tools/ERP.DbMigrator --output-dir Migrations/Master
///   dotnet ef database update --context MasterDbContext --project tools/ERP.DbMigrator
/// </summary>
public class MasterDbContextFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        // Read connection string from the API host's appsettings
        var configuration = new ConfigurationBuilder()
            .SetBasePath(FindSolutionRoot())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var connectionString = configuration.GetConnectionString("MasterDb")
            ?? throw new InvalidOperationException(
                "MasterDb connection string not found. Ensure appsettings.Development.json has ConnectionStrings:MasterDb.");

        // Register Identity entity configurations (same as runtime — uses the Identity assembly)
        var identityAssembly = typeof(TenantConfiguration).Assembly;
        if (!MasterDbContext.ConfigurationAssemblies.Contains(identityAssembly))
            MasterDbContext.ConfigurationAssemblies.Add(identityAssembly);

        var optionsBuilder = new DbContextOptionsBuilder<MasterDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
        {
            // Migrations live in the SqlServer provider project
            sqlOptions.MigrationsAssembly(
                typeof(ERP.Persistence.SqlServer.Extensions.SqlServerServiceExtensions).Assembly.FullName);
        });

        return new MasterDbContext(optionsBuilder.Options);
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
