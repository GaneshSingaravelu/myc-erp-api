using ERP.Production.Application.Commands;
using ERP.Production.Application.Queries;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Production.Api.Controllers;

/// <summary>
/// PLANTS API — CRUD for manufacturing plants within a tenant.
///
/// GET    /api/v1/plants              → List all plants (active only by default)
/// GET    /api/v1/plants/{id}         → Get a single plant
/// POST   /api/v1/plants              → Create a plant (admin)
/// PUT    /api/v1/plants/{id}         → Update a plant (admin)
/// DELETE /api/v1/plants/{id}         → Delete a plant (admin)
/// </summary>
[ApiController]
[Route("api/v1/plants")]
[Authorize]
public class PlantsController : ControllerBase
{
    private readonly IMediator _mediator;

    public PlantsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool activeOnly = true)
    {
        var result = await _mediator.Send(new GetPlantsQuery(activeOnly));
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _mediator.Send(new GetPlantByIdQuery(id));
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create([FromBody] CreatePlantRequest request)
    {
        var result = await _mediator.Send(new CreatePlantCommand(request.Code, request.Name, request.Location));
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value)
            : BadRequest(result.Error);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePlantRequest request)
    {
        var result = await _mediator.Send(new UpdatePlantCommand(id, request.Name, request.Location, request.IsActive));
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _mediator.Send(new DeletePlantCommand(id));
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }
}

public record CreatePlantRequest(string Code, string Name, string? Location);
public record UpdatePlantRequest(string Name, string? Location, bool IsActive);
