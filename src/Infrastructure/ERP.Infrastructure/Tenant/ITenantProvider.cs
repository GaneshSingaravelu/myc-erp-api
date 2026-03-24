namespace ERP.Infrastructure.Tenant;

/// <summary>
/// TENANT PROVIDER — Provides the current tenant for the current HTTP request.
///
/// HOW IT WORKS:
/// ==============
/// 1. Request arrives with header: X-Tenant-Id: abc-manufacturing
/// 2. TenantMiddleware reads the header, looks up the tenant, stores it in HttpContext
/// 3. ITenantProvider retrieves it from HttpContext whenever needed
///
/// WHO USES THIS?
/// ===============
/// - TenantDbContextFactory: to get the connection string for the correct database
/// - CurrentUserService: to include tenant info in audit trails
/// - Any service that needs tenant-specific behavior
///
/// REGISTERED AS: Scoped (one instance per HTTP request)
/// </summary>
public interface ITenantProvider
{
    /// <summary>The current tenant's identifier (e.g., "abc-manufacturing")</summary>
    string TenantId { get; }

    /// <summary>Full tenant information including connection string and provider</summary>
    TenantInfo GetCurrentTenant();
}
