using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers.v1;

/// <summary>
/// TENANT MANAGEMENT CONTROLLER — Admin-only endpoints operating on the Master DB.
///
/// WITHDRAWN PLATFORM OPERATIONS (S-T1)
/// ====================================
/// Create, Deactivate and Migrate are withdrawn and answer a fixed 405. Each was a
/// PLATFORM operation reachable by any tenant Admin against any target tenant, and this
/// application has no platform-administration role: "Admin" is scoped to the tenant chosen
/// at login, through UserTenants. Scoping them was not possible without inventing privilege
/// semantics the product does not have, so the capabilities were removed instead. Tenant
/// provisioning therefore has no HTTP channel at present — a replacement is a separate
/// product decision, and the frontend Tenants page still calls these routes.
///
/// AUTHORIZATION: Requires the "Admin" role. That role is tenant-scoped, so it does NOT
/// mean platform administrator.
///
/// NOTE: "/api/v1/tenants" is in TenantMiddleware's skip list, so no X-Tenant-Id header is
/// required and HttpContext.Items["CurrentTenant"] is NOT populated for these endpoints.
/// ITenantProvider.GetCurrentTenant() throws here. Scoping the remaining read/update
/// endpoints to the caller's tenant has to resolve that first.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize(Roles = "Admin")]
public class TenantsController : ControllerBase
{
    private readonly ITenantManagementService _tenantService;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly ILogger<TenantsController> _logger;

    public TenantsController(
        ITenantManagementService tenantService,
        ITenantProvisioningService provisioningService,
        ILogger<TenantsController> logger)
    {
        _tenantService = tenantService;
        _provisioningService = provisioningService;
        _logger = logger;
    }

    /// <summary>
    /// Creating a tenant is withdrawn over HTTP (S-T1).
    ///
    /// <para>Provisioning a tenant is a PLATFORM operation: it creates a record no tenant
    /// owns, and it opens an outbound database connection to a host supplied by the caller.
    /// This application has no platform-administration model — "Admin" means Admin *of the
    /// selected tenant*, scoped at login through UserTenants — so there is no authority that
    /// could safely be checked here. The capability is withdrawn rather than handed to every
    /// tenant Admin.</para>
    ///
    /// <para>The answer is a constant, and the request body is not bound at all: no tenant
    /// record is created, no connection string is stored, and no outbound connection is
    /// attempted. Because nothing is read, the response cannot vary with the payload.</para>
    /// </summary>
    [HttpPost]
    public IActionResult Create()
        => StatusCode(405, new { error = "Creating tenants is not supported." });

    /// <summary>
    /// Get all tenants.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var result = await _tenantService.GetAllAsync();
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    /// <summary>
    /// Get a tenant by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _tenantService.GetByIdAsync(id);
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    /// <summary>
    /// Update tenant details (name, active status).
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTenantRequest request)
    {
        var result = await _tenantService.UpdateAsync(id, request);
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    /// <summary>
    /// Deactivating a tenant is withdrawn over HTTP (S-T1).
    ///
    /// <para>Setting a tenant inactive locks every user of that tenant out — TenantMiddleware
    /// refuses their requests with 403 and login fails — so it is a platform-level
    /// availability control, not a tenant-local edit. The target was an arbitrary id, so any
    /// tenant Admin could disable any other tenant.</para>
    ///
    /// <para>The answer is a constant. The target is never looked up, so this endpoint cannot
    /// be used to learn whether an id names a real tenant.</para>
    /// </summary>
    [HttpDelete("{id:guid}")]
    public IActionResult Deactivate(Guid id)
        => StatusCode(405, new { error = "Deactivating tenants is not supported." });

    /// <summary>
    /// Tenant database migration is withdrawn over HTTP (S-T1).
    ///
    /// <para>This action read the TARGET tenant's connection string and ran schema migrations
    /// against that database. Both the credential read and the migration are platform
    /// operations, and the target was an arbitrary id, so any tenant Admin could reach another
    /// tenant's database. The private helper that fetched the connection string is removed
    /// with it — withdrawal has to remove the mechanism, not just the route.</para>
    ///
    /// <para>The answer is a constant. No tenant is looked up, no connection string is
    /// retrieved, and no database is contacted.</para>
    /// </summary>
    [HttpPost("{id:guid}/migrate")]
    public IActionResult Migrate(Guid id)
        => StatusCode(405, new { error = "Tenant database migration is not supported." });
}
