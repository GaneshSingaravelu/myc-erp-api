using System.Security.Claims;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.MultiTenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers.v1;

/// <summary>
/// MENUS CONTROLLER — API-driven sidebar navigation for Angular.
///
/// After login, Angular calls GET /api/v1/menus/my-menus to build the sidebar.
/// The response is filtered by the user's permissions and tenant feature flags.
/// Angular caches this in the NgRx Signal Store and rebuilds only on tenant switch.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class MenusController : ControllerBase
{
    private readonly IMenuService _menuService;

    public MenusController(IMenuService menuService)
    {
        _menuService = menuService;
    }

    /// <summary>
    /// GET /api/v1/menus/my-menus
    /// Returns the permission-filtered menu tree for the current user.
    ///
    /// Tenant resolution: TenantMiddleware already resolved X-Tenant-Id and
    /// stashed the TenantInfo in HttpContext.Items["CurrentTenant"]. We use
    /// that Guid here — NOT the "tenant_id" claim, which holds the Code
    /// (human-readable Identifier) and would fail a Guid.TryParse.
    /// </summary>
    [HttpGet("my-menus")]
    public async Task<IActionResult> GetMyMenus()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        // Read the tenant resolved by TenantMiddleware.
        // tenantId is nullable so feature-flag filtering degrades gracefully
        // if the middleware was bypassed (e.g., in tests).
        Guid? tenantId = HttpContext.Items["CurrentTenant"] is TenantInfo tenant
            ? tenant.Id
            : null;

        var result = await _menuService.GetMyMenusAsync(userId, tenantId);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }
}
