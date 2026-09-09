using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService)
    {
        _userService = userService;
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAll()
    {
        var result = await _userService.GetAllAsync();
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error.Message });
    }

    // Admin-only, like every other action here. Its absence was a confirmed
    // authorization defect: any authenticated principal — Viewer included — could
    // read any user by id. Tenant scoping is enforced separately in UserService;
    // the role gate alone is not sufficient.
    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _userService.GetByIdAsync(id);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error.Message });
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        var result = await _userService.CreateAsync(request);

        if (result.IsFailure)
            return Conflict(new { error = result.Error.Message });

        return CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request)
    {
        var result = await _userService.UpdateAsync(id, request);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error.Message });
    }

    /// <summary>
    /// Deleting users is not available through this API.
    ///
    /// <para>The underlying operation soft-deletes the GLOBAL identity: the AppUser row
    /// is flagged deleted and a global query filter then hides it from every tenant,
    /// while its UserTenant and AppUserRole rows are left behind. A tenant
    /// administrator invoking it therefore removes the user from tenants they have no
    /// authority over. Every other operation on this controller is scoped to the
    /// caller's tenant; this one never was.</para>
    ///
    /// <para>Removing a user from a single tenant is the operation this endpoint
    /// should probably express, but doing that safely also requires deciding what
    /// happens to the last membership, how an ejected identity is recovered, and how
    /// an already-issued token is revoked — none of which exist yet. Rather than ship
    /// either an unsafe global delete or a silently redefined one, the capability is
    /// withdrawn until that lifecycle is designed.</para>
    ///
    /// <para>The route is kept so callers get a deterministic, explained refusal
    /// instead of the framework's empty automatic 405. The Admin requirement is kept
    /// too: authorization still runs first, so a non-Admin is rejected before reaching
    /// this action rather than being told the operation does not exist.</para>
    ///
    /// <para>The response is identical for a real id and an unknown one. Probing the
    /// target would turn a disabled endpoint into an existence oracle.</para>
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public IActionResult Delete(Guid id)
        => StatusCode(405, new { error = "Deleting users is not supported." });

    [HttpPost("{id:guid}/roles")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AssignRoles(Guid id, [FromBody] AssignRolesRequest request)
    {
        var result = await _userService.AssignRolesAsync(id, request);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error.Message });
    }

    [HttpGet("{id:guid}/roles")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetUserRoles(Guid id)
    {
        var result = await _userService.GetUserRoleAssignmentsAsync(id);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error.Message });
    }

    [HttpGet("{id:guid}/tenants")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetUserTenants(Guid id)
    {
        var result = await _userService.GetUserTenantsAsync(id);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error.Message });
    }

    [HttpPut("{id:guid}/tenants")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AssignTenants(Guid id, [FromBody] AssignTenantsRequest request)
    {
        var result = await _userService.AssignTenantsAsync(id, request);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error.Message });
    }
}
