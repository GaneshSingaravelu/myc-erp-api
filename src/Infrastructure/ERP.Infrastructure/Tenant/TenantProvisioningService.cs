using ERP.Application.Abstractions.MultiTenancy;
using ERP.Persistence.Context;
using ERP.Persistence.PostgreSql.Extensions;
using ERP.Persistence.SqlServer.Extensions;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP.Infrastructure.Tenant;

/// <summary>
/// TENANT PROVISIONING — Creates databases, runs migrations for new tenants.
///
/// HOW IT WORKS:
/// ==============
/// 1. Build a DbContextOptions with the tenant's connection string + provider
/// 2. Create an AppDbContext with those options
/// 3. Call context.Database.MigrateAsync() — EF Core creates the DB and applies all migrations
///
/// EF Core's MigrateAsync() is idempotent:
///   - If DB doesn't exist → creates it + applies all migrations
///   - If DB exists but has pending migrations → applies only the new ones
///   - If DB is up-to-date → does nothing
///
/// REGISTERED AS: Scoped (needs fresh DbContext per operation)
/// </summary>
public class TenantProvisioningService : ITenantProvisioningService
{
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(ILogger<TenantProvisioningService> logger)
    {
        _logger = logger;
    }

    public async Task<Result> ProvisionTenantDatabaseAsync(
        string connectionString,
        string dbProvider,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Provisioning tenant database. Provider: {DbProvider}", dbProvider);

            var options = BuildDbContextOptions(connectionString, dbProvider);
            await using var context = new AppDbContext(options);

            // EnsureCreatedAsync would skip migrations — never use it with migrations
            // MigrateAsync creates the DB if needed AND applies all migrations
            await context.Database.MigrateAsync(cancellationToken);

            _logger.LogInformation("Tenant database provisioned successfully");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision tenant database");
            return Result.Failure(Error.Validation(
                "Tenant.ProvisionFailed",
                $"Failed to provision tenant database: {ex.Message}"));
        }
    }

    public async Task<Result> MigrateTenantDatabaseAsync(
        string connectionString,
        string dbProvider,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Migrating tenant database. Provider: {DbProvider}", dbProvider);

            var options = BuildDbContextOptions(connectionString, dbProvider);
            await using var context = new AppDbContext(options);

            var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken);
            var pending = pendingMigrations.ToList();

            if (pending.Count == 0)
            {
                _logger.LogInformation("No pending migrations for tenant database");
                return Result.Success();
            }

            _logger.LogInformation("Applying {Count} pending migrations", pending.Count);
            await context.Database.MigrateAsync(cancellationToken);

            _logger.LogInformation("Tenant database migrated successfully");
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate tenant database");
            return Result.Failure(Error.Validation(
                "Tenant.MigrationFailed",
                $"Failed to migrate tenant database: {ex.Message}"));
        }
    }

    /// <summary>
    /// Build DbContextOptions for the given connection string and provider.
    /// Same logic as TenantDbContextFactory but for one-off provisioning operations.
    /// </summary>
    private static DbContextOptions<AppDbContext> BuildDbContextOptions(
        string connectionString, string dbProvider)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        if (dbProvider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
            optionsBuilder.UsePostgreSqlProvider(connectionString);
        else
            optionsBuilder.UseSqlServerProvider(connectionString);

        return optionsBuilder.Options;
    }
}
