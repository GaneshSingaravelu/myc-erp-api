using ERP.Application.Abstractions.MultiTenancy;
using ERP.Persistence.Context;
using ERP.Persistence.PostgreSql.Extensions;
using ERP.Persistence.Providers;
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
    ///
    /// FAILS CLOSED. An unrecognised provider throws; it is never treated as SQL Server.
    /// This used to fall through to SQL Server for anything that was not "PostgreSql",
    /// meaning a typo — or a null — would quietly provision the wrong engine. Like
    /// TenantDbContextFactory, it now refuses rather than guessing; unlike that factory,
    /// the value here arrives as a method argument, so the diagnostic points at the supplied
    /// value instead of at the Database:Provider configuration key.
    ///
    /// Recognition itself is delegated to DatabaseProviderResolver so there is exactly one
    /// provider vocabulary in the codebase. TryParse is used rather than Parse because
    /// Parse's message instructs the operator to set Database:Provider, which would be
    /// misleading for a value that was never read from there.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="dbProvider"/> is null, blank or not a supported provider.
    /// </exception>
    private static DbContextOptions<AppDbContext> BuildDbContextOptions(
        string connectionString, string dbProvider)
    {
        if (!DatabaseProviderResolver.TryParse(dbProvider, out var provider))
        {
            throw new InvalidOperationException(
                $"Invalid tenant database provider: {DescribeProviderValue(dbProvider)}. " +
                $"Supported values: {DatabaseProviderResolver.SupportedProviders}");
        }

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

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
                    $"Unsupported tenant database provider: '{provider}'. " +
                    $"Supported values: {DatabaseProviderResolver.SupportedProviders}");
        }

        return optionsBuilder.Options;
    }

    /// <summary>
    /// Renders a rejected provider value for a diagnostic, distinguishing null from an
    /// empty or whitespace string — otherwise all three read as an empty pair of quotes and
    /// the operator cannot tell which was supplied.
    /// </summary>
    private static string DescribeProviderValue(string? dbProvider) => dbProvider switch
    {
        null => "null",
        "" => "empty",
        _ when string.IsNullOrWhiteSpace(dbProvider) => $"whitespace ('{dbProvider}')",
        _ => $"'{dbProvider}'",
    };
}
