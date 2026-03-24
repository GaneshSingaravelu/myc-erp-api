using Microsoft.AspNetCore.Http;

namespace ERP.Infrastructure.Tenant;

/// <summary>
/// HTTP CONTEXT TENANT PROVIDER — Retrieves the current tenant from HttpContext.
///
/// HOW IT WORKS:
/// ==============
/// TenantMiddleware stores the tenant in HttpContext.Items["CurrentTenant"].
/// This class reads it back. It's a simple accessor pattern.
///
/// WHAT IS IHttpContextAccessor? (Interview Question!)
/// ====================================================
/// In ASP.NET Core, HttpContext is NOT available everywhere (unlike ASP.NET Framework).
/// Services that need to access HttpContext must use IHttpContextAccessor.
///
/// IHttpContextAccessor uses AsyncLocal&lt;T&gt; internally — a thread-safe way to
/// store data that flows with the async execution context.
///
/// REGISTERED AS: Scoped (one instance per HTTP request)
///
/// WHY NOT INJECT HttpContext DIRECTLY?
/// =====================================
/// HttpContext only exists during an HTTP request. Background services, health checks,
/// and tests don't have an HttpContext. IHttpContextAccessor returns null gracefully
/// in those cases, which we handle with the exception.
/// </summary>
public class HttpContextTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string TenantId => GetCurrentTenant().Identifier;

    public TenantInfo GetCurrentTenant()
    {
        var httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "HttpContext is not available. This service can only be used during an HTTP request.");

        var tenant = httpContext.Items["CurrentTenant"] as TenantInfo
            ?? throw new InvalidOperationException(
                "Tenant information not found in HttpContext. " +
                "Ensure TenantMiddleware is registered in the pipeline before this service is used.");

        return tenant;
    }
}
