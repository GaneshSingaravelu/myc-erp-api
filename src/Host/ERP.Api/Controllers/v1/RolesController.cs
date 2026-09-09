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

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _roleService.DeleteAsync(id);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error.Message });
    }

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
