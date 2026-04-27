using ERP.SharedKernel.Results;

namespace ERP.Application.Abstractions.MultiTenancy;

/// <summary>
/// TENANT PROVISIONING SERVICE — Creates and migrates tenant databases.
///
/// WHAT IS PROVISIONING?
/// =====================
/// When a new company signs up for the ERP, we need to:
/// 1. Create their database (ERP_{TenantCode})
/// 2. Run all EF Core migrations to create tables
/// 3. Seed default data (plants, preferences, etc.)
///
/// This happens automatically via the Admin API — no manual SQL scripts needed.
/// </summary>
public interface ITenantProvisioningService
{
    /// <summary>
    /// Create the tenant database and apply all migrations.
    /// Returns success/failure with details.
    /// </summary>
    Task<Result> ProvisionTenantDatabaseAsync(
        string connectionString,
        string dbProvider,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply pending migrations to an existing tenant database.
    /// Used when deploying new versions with schema changes.
    /// </summary>
    Task<Result> MigrateTenantDatabaseAsync(
        string connectionString,
        string dbProvider,
        CancellationToken cancellationToken = default);
}
