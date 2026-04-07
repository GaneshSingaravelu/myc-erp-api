using System.Security.Claims;
using ERP.Identity.Services;
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
    /// </summary>
    [HttpGet("my-menus")]
    public async Task<IActionResult> GetMyMenus()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var tenantIdClaim = User.FindFirst("tenant_id")?.Value;
        Guid? tenantId = Guid.TryParse(tenantIdClaim, out var tid) ? tid : null;

        var result = await _menuService.GetMyMenusAsync(userId, tenantId);
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }
}
