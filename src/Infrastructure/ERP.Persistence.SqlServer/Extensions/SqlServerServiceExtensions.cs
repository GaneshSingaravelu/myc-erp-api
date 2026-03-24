using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Persistence.SqlServer.Extensions;

/// <summary>
/// SQL SERVER PROVIDER REGISTRATION — Configures EF Core to use SQL Server.
///
/// WHY A SEPARATE PROJECT FOR EACH DATABASE PROVIDER?
/// ===================================================
/// 1. CLEAN SEPARATION: SQL Server-specific code doesn't pollute PostgreSQL code
/// 2. MIGRATIONS: Each provider has its own migration files (SQL syntax differs)
/// 3. OPTIONAL: A client using PostgreSQL doesn't need SQL Server NuGet packages
/// 4. TESTABILITY: Easy to swap providers in tests
///
/// HOW IT CONNECTS TO MULTI-TENANCY:
/// ==================================
/// This method is called by TenantDbContextFactory when a tenant uses SQL Server.
/// The factory reads the tenant's DbProvider setting and calls the right extension.
/// </summary>
public static class SqlServerServiceExtensions
{
    /// <summary>
    /// Configure DbContextOptions to use SQL Server with the given connection string.
    /// Called by TenantDbContextFactory per-request based on tenant configuration.
    /// </summary>
    public static DbContextOptionsBuilder UseSqlServerProvider(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString)
    {
        optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
        {
            // Set the assembly where SQL Server migrations live
            sqlOptions.MigrationsAssembly(typeof(SqlServerServiceExtensions).Assembly.FullName);

            // Enable retry on transient failures (network blips, deadlocks)
            // EF Core will automatically retry failed queries up to 3 times
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null);

            // Set command timeout to 30 seconds (default is 30, being explicit)
            sqlOptions.CommandTimeout(30);
        });

        return optionsBuilder;
    }

    /// <summary>
    /// Register SQL Server as the default database provider (for single-tenant or dev scenarios).
    /// </summary>
    public static IServiceCollection AddSqlServerPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServerProvider(connectionString));

        return services;
    }
}
