namespace ERP.Application.Abstractions.MultiTenancy;

/// <summary>
/// TENANT STORE — Retrieves tenant information from the master database.
///
/// WHAT IS THE MASTER DATABASE?
/// ==============================
/// The master database is a central database that stores:
/// - Tenant list (which companies use the ERP)
/// - Tenant connection strings (which database to connect to)
/// - Tenant configuration (which DB provider, which features are enabled)
///
/// The master DB does NOT store any business data (no job cards, work orders, etc.).
/// Business data lives in each tenant's own database.
///
/// WHY AN INTERFACE?
/// ==================
/// For now, we'll use a simple in-memory/config-based store.
/// Later, we'll swap this with an EF Core-based store that reads from a real master DB.
/// Because we depend on ITenantStore (interface), the swap requires NO code changes
/// in TenantMiddleware or any other consumer.
///
/// This is DEPENDENCY INVERSION PRINCIPLE + STRATEGY PATTERN in action.
/// </summary>
public interface ITenantStore
{
    /// <summary>Look up a tenant by their identifier (e.g., "abc-manufacturing")</summary>
    Task<TenantInfo?> GetByIdentifierAsync(string identifier);

    /// <summary>Get all registered tenants</summary>
    Task<IReadOnlyList<TenantInfo>> GetAllAsync();
}
