using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Persistence.PostgreSql.Extensions;

/// <summary>
/// POSTGRESQL PROVIDER REGISTRATION — Configures EF Core to use PostgreSQL (via Npgsql).
///
/// WHY POSTGRESQL? (Good to know for interviews)
/// ===============================================
/// - Free and open source (no licensing costs — great for client deployments)
/// - Excellent performance for complex queries
/// - Full ACID compliance (important for ERP financial transactions)
/// - JSON support (useful for flexible workflow step configurations)
/// - Cross-platform (runs on Windows, Linux, Docker)
///
/// NPGSQL:
/// =======
/// Npgsql is the .NET data provider for PostgreSQL. It's the bridge between
/// EF Core and PostgreSQL, just like Microsoft.EntityFrameworkCore.SqlServer
/// is the bridge for SQL Server.
/// </summary>
public static class PostgreSqlServiceExtensions
{
    /// <summary>
    /// Configure DbContextOptions to use PostgreSQL with the given connection string.
    /// Called by TenantDbContextFactory per-request based on tenant configuration.
    /// </summary>
    public static DbContextOptionsBuilder UsePostgreSqlProvider(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString)
    {
        optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
        {
            // Set the assembly where PostgreSQL migrations live
            npgsqlOptions.MigrationsAssembly(typeof(PostgreSqlServiceExtensions).Assembly.FullName);

            // Enable retry on transient failures
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null);

            // Set command timeout
            npgsqlOptions.CommandTimeout(30);
        });

        return optionsBuilder;
    }

    /// <summary>
    /// Register PostgreSQL as the default database provider (for single-tenant or dev scenarios).
    /// </summary>
    public static IServiceCollection AddPostgreSqlPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UsePostgreSqlProvider(connectionString));

        return services;
    }
}
