using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers.v1;

/// <summary>
/// TENANT MANAGEMENT CONTROLLER — Admin-only endpoints for managing tenants.
///
/// PROVISIONING FLOW:
/// ===================
/// POST /api/v1/tenants →
///   1. TenantManagementService.Create() — saves Tenant record to Master DB
///   2. TenantProvisioningService.Provision() — creates tenant DB + runs migrations
///   3. Returns the new tenant info
///
/// If provisioning fails, the tenant record is kept (IsActive=false) so admin
/// can retry provisioning without re-entering all details.
///
/// AUTHORIZATION: Requires "Admin" role (only system admins can manage tenants).
/// NOTE: No X-Tenant-Id header needed — these endpoints operate on the Master DB.
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
    /// Create a new tenant and provision its database.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request)
    {
        // Step 1: Create tenant record in Master DB
        var createResult = await _tenantService.CreateAsync(request);
        if (!createResult.IsSuccess)
            return BadRequest(createResult.Error);

        var tenant = createResult.Value;

        // Step 2: Provision the tenant database (create + migrate)
        var provisionResult = await _provisioningService.ProvisionTenantDatabaseAsync(
            request.ConnectionString, request.DbProvider);

        if (!provisionResult.IsSuccess)
        {
            _logger.LogError("Tenant '{TenantCode}' created but database provisioning failed: {Error}",
                request.Code, provisionResult.Error.Message);

            // Return partial success — tenant record exists but DB provisioning failed
            // Admin can retry via POST /api/v1/tenants/{id}/migrate
            return StatusCode(207, new
            {
                Tenant = tenant,
                ProvisioningError = provisionResult.Error.Message,
                Message = "Tenant record created but database provisioning failed. Use the migrate endpoint to retry."
            });
        }

        _logger.LogInformation("Tenant '{TenantCode}' fully provisioned", request.Code);
        return CreatedAtAction(nameof(GetById), new { id = tenant.Id }, tenant);
    }

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
    /// Deactivate a tenant (users can no longer log in).
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var result = await _tenantService.DeactivateAsync(id);
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }

    /// <summary>
    /// Retry database provisioning for a tenant whose initial provisioning failed.
    /// Also used to apply pending migrations to an existing tenant database.
    /// </summary>
    [HttpPost("{id:guid}/migrate")]
    public async Task<IActionResult> Migrate(Guid id)
    {
        // Get tenant to read connection string and provider
        var tenantResult = await _tenantService.GetByIdAsync(id);
        if (!tenantResult.IsSuccess)
            return NotFound(tenantResult.Error);

        // We need the connection string, which isn't in the response DTO for security.
        // Re-fetch from the underlying store.
        var result = await _provisioningService.MigrateTenantDatabaseAsync(
            await GetTenantConnectionStringAsync(id),
            tenantResult.Value.DbProvider);

        return result.IsSuccess
            ? Ok(new { Message = "Database migration completed successfully." })
            : BadRequest(result.Error);
    }

    /// <summary>
    /// Helper to get connection string directly (not exposed in DTOs).
    /// </summary>
    private async Task<string> GetTenantConnectionStringAsync(Guid tenantId)
    {
        // Access the MasterDbContext directly via the tenant management service's backing store
        // This is a pragmatic shortcut — the controller is already in the composition root
        var tenantStore = HttpContext.RequestServices.GetRequiredService<ITenantStore>();
        var allTenants = await tenantStore.GetAllAsync();
        var tenant = allTenants.FirstOrDefault(t => t.Id == tenantId);
        return tenant?.ConnectionString
            ?? throw new InvalidOperationException($"Tenant {tenantId} connection string not found.");
    }
}
