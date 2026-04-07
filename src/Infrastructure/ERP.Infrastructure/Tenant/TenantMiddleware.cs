using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ERP.Infrastructure.Tenant;

/// <summary>
/// TENANT MIDDLEWARE — Identifies the tenant for every incoming HTTP request.
///
/// WHAT IS MIDDLEWARE? (Very Common Interview Question!)
/// =====================================================
/// Middleware is code that runs in the ASP.NET Core request pipeline.
/// Every HTTP request passes through a chain of middleware components:
///
///   Request → [Logging] → [Auth] → [TenantMiddleware] → [Routing] → Controller
///   Response ← [Logging] ← [Auth] ← [TenantMiddleware] ← [Routing] ← Controller
///
/// Each middleware can:
/// - Process the request BEFORE passing it to the next middleware
/// - Process the response AFTER the next middleware returns
/// - SHORT-CIRCUIT the pipeline (return early without calling next)
///
/// WHAT THIS MIDDLEWARE DOES:
/// ==========================
/// 1. Reads the "X-Tenant-Id" header from the request
/// 2. Looks up the tenant in the master database (with caching!)
/// 3. Stores the tenant info in HttpContext.Items (available to all downstream code)
/// 4. If tenant not found → returns 400/404 (short-circuits the pipeline)
///
/// WHY CACHING?
/// =============
/// We cache tenant lookups in IMemoryCache for 30 minutes.
/// Without caching: Every request hits the master database = slow
/// With caching: Only the first request per tenant hits the DB, then it's instant
///
/// REGISTRATION IN Program.cs:
/// ============================
///   app.UseMiddleware&lt;TenantMiddleware&gt;();  // Add to pipeline
///
/// ORDER MATTERS: Middleware runs in the ORDER it's registered.
/// TenantMiddleware must run AFTER authentication (so JWT is validated first)
/// but BEFORE routing (so the correct DB is connected before handlers run).
///
/// DESIGN PATTERN: Middleware Pattern / Chain of Responsibility
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;
    private const string TenantHeaderName = "X-Tenant-Id";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Runs for every HTTP request. This is the middleware's main method.
    ///
    /// PARAMETER EXPLANATION:
    /// - HttpContext: Contains everything about the current request (headers, body, user, etc.)
    /// - ITenantStore: Injected PER REQUEST by DI (middleware supports method injection)
    /// - IMemoryCache: Singleton cache shared across all requests
    /// </summary>
    public async Task InvokeAsync(
        HttpContext context,
        ITenantStore tenantStore,
        IMemoryCache cache)
    {
        // Skip tenant resolution for certain paths (health checks, swagger, etc.)
        var path = context.Request.Path.Value?.ToLower() ?? "";
        if (ShouldSkipTenantResolution(path))
        {
            await _next(context);
            return;
        }

        // Step 1: Read tenant identifier from request header
        if (!context.Request.Headers.TryGetValue(TenantHeaderName, out var tenantIdentifier)
            || string.IsNullOrWhiteSpace(tenantIdentifier))
        {
            _logger.LogWarning("Request missing {Header} header from {IP}",
                TenantHeaderName, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Missing required header: {TenantHeaderName}",
                code = "TENANT_HEADER_MISSING"
            });
            return; // SHORT-CIRCUIT: Don't call _next, request stops here
        }

        // Step 2: Look up tenant (with caching to avoid hitting master DB every request)
        var cacheKey = $"tenant:{tenantIdentifier}";
        if (!cache.TryGetValue(cacheKey, out TenantInfo? tenant))
        {
            // Cache miss — query the tenant store (master database)
            tenant = await tenantStore.GetByIdentifierAsync(tenantIdentifier!);

            if (tenant is not null)
            {
                // Cache the tenant info for 30 minutes
                cache.Set(cacheKey, tenant, CacheDuration);
                _logger.LogDebug("Cached tenant {TenantId} for {Duration}", tenantIdentifier, CacheDuration);
            }
        }

        // Step 3: Validate tenant exists and is active
        if (tenant is null)
        {
            _logger.LogWarning("Tenant not found: {TenantId}", tenantIdentifier);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"Tenant '{tenantIdentifier}' not found",
                code = "TENANT_NOT_FOUND"
            });
            return;
        }

        if (!tenant.IsActive)
        {
            _logger.LogWarning("Inactive tenant attempted access: {TenantId}", tenantIdentifier);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Tenant account is inactive. Contact administrator.",
                code = "TENANT_INACTIVE"
            });
            return;
        }

        // Step 4: SECURITY — Cross-check tenant header against JWT tenant_id claim
        // If the user is authenticated, their token contains a tenant_id claim set at login.
        // The X-Tenant-Id header MUST match. This prevents authenticated users from
        // pivoting to a different tenant's data by simply changing the header.
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tokenTenantId = context.User.FindFirst("tenant_id")?.Value;
            if (tokenTenantId is not null
                && !string.Equals(tokenTenantId, tenantIdentifier.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SECURITY: Tenant mismatch — header={HeaderTenant}, token={TokenTenant}, user={User}, IP={IP}",
                    tenantIdentifier, tokenTenantId,
                    context.User.Identity.Name, context.Connection.RemoteIpAddress);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Tenant context mismatch. The X-Tenant-Id header does not match your authentication context.",
                    code = "TENANT_MISMATCH"
                });
                return;
            }
        }

        // Step 5: Store tenant info in HttpContext.Items for downstream use
        context.Items["CurrentTenant"] = tenant;

        _logger.LogDebug("Resolved tenant: {TenantName} ({TenantId})", tenant.Name, tenant.Identifier);

        // Step 6: Call the next middleware in the pipeline
        await _next(context);
    }

    /// <summary>
    /// Some endpoints don't need tenant resolution (public endpoints).
    /// </summary>
    private static bool ShouldSkipTenantResolution(string path)
    {
        return path.StartsWith("/health")
            || path.StartsWith("/swagger")
            || path.StartsWith("/api/v1/auth/login")
            || path.StartsWith("/api/v1/tenants")
            || path.StartsWith("/hubs/");
    }
}
