using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers.v1;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize(Roles = "Admin")]
public class RolesController : ControllerBase
{
    private readonly IRoleService _roleService;

    public RolesController(IRoleService roleService)
    {
        _roleService = roleService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var result = await _roleService.GetAllAsync();
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error.Message });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _roleService.GetByIdAsync(id);
        return result.IsSuccess ? Ok(result.Value) : NotFound(new { error = result.Error.Message });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRoleRequest request)
    {
        var result = await _roleService.CreateAsync(request);

        if (result.IsFailure)
            return Conflict(new { error = result.Error.Message });

        return CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value);
    }

    // A role this tenant may not see answers 404, exactly as GET does, so a caller holding a
    // GUID cannot learn from the status code whether it names a real role in another tenant.
    // Every other refusal — system role, unowned role — stays a 400, because those roles are
    // deliberately visible and naming them discloses nothing.
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRoleRequest request)
    {
        var result = await _roleService.UpdateAsync(id, request);

        if (result.IsSuccess)
            return Ok(result.Value);

        return result.Error.Code switch
        {
            "Role.NotFound" => NotFound(new { error = result.Error.Message }),
            "Role.NameExists" => Conflict(new { error = result.Error.Message }),
            _ => BadRequest(new { error = result.Error.Message })
        };
    }

    /// <summary>
    /// Role deletion is withdrawn, not tenant-scoped.
    ///
    /// <para>The service primitive behind it deletes by raw id with no tenant check, and
    /// AppRole is auditable while AppUserRole and AppRolePermission are not — so a delete is
    /// converted to a soft delete that no cascade follows, leaving assignment and permission
    /// rows pointing at a role that queries no longer return. Users silently lose what the
    /// role granted while the junction rows still claim they hold it. Defining a correct
    /// lifecycle for that is a separate decision, so the capability is withdrawn rather than
    /// redefined.</para>
    ///
    /// <para>The answer is a constant. Nothing is looked up, so this endpoint cannot be used
    /// to learn whether a given id names a real role in some other tenant.</para>
    /// </summary>
    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid id)
        => StatusCode(405, new { error = "Deleting roles is not supported." });

    [HttpPost("{id:guid}/permissions")]
    public async Task<IActionResult> AssignPermissions(Guid id, [FromBody] AssignPermissionsRequest request)
    {
        var result = await _roleService.AssignPermissionsAsync(id, request);

        if (result.IsSuccess)
            return NoContent();

        // Only the ROLE being invisible is a 404. An unknown permission id in the body stays
        // a 400 — it is a malformed request, not a hidden resource.
        return result.Error.Code == "Role.NotFound"
            ? NotFound(new { error = result.Error.Message })
            : BadRequest(new { error = result.Error.Message });
    }

    [HttpGet("permissions")]
    public async Task<IActionResult> GetAllPermissions()
    {
        var result = await _roleService.GetAllPermissionsAsync();
        return result.IsSuccess ? Ok(result.Value) : BadRequest(new { error = result.Error.Message });
    }
}
