using ERP.Production.Application.Commands;
using ERP.Production.Application.DTOs;
using ERP.Production.Application.Queries;
using ERP.Production.Domain.Enums;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Production.Api.Controllers;

/// <summary>
/// WORK ORDERS API — CRUD + lifecycle endpoints for production work orders.
///
/// API ENDPOINTS:
/// ==============
/// GET    /api/v1/production/work-orders                → List all work orders
/// GET    /api/v1/production/work-orders/{id}           → Get work order with job cards
/// POST   /api/v1/production/work-orders                → Create new work order (Draft)
/// PUT    /api/v1/production/work-orders/{id}           → Update work order (Draft only)
/// DELETE /api/v1/production/work-orders/{id}           → Soft-delete work order
/// POST   /api/v1/production/work-orders/{id}/release   → Release to production (generates job cards)
/// </summary>
[ApiController]
[Route("api/v1/production/work-orders")]
[Authorize]
public class WorkOrdersController : ControllerBase
{
    private readonly IMediator _mediator;

    public WorkOrdersController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] WorkOrderStatus? status = null)
    {
        var result = await _mediator.Send(new GetWorkOrdersQuery(status));
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _mediator.Send(new GetWorkOrderByIdQuery(id));
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkOrderRequest request)
    {
        var command = new CreateWorkOrderCommand(
            request.OrderNumber,
            request.ProductName,
            request.Quantity,
            request.ProcessTemplateId,
            request.PlantId,
            request.DueDate,
            request.Notes);

        var result = await _mediator.Send(command);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value)
            : BadRequest(result.Error);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWorkOrderRequest request)
    {
        var command = new UpdateWorkOrderCommand(
            id, request.ProductName, request.Quantity, request.DueDate, request.Notes);

        var result = await _mediator.Send(command);
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _mediator.Send(new DeleteWorkOrderCommand(id));
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }

    [HttpPost("{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id)
    {
        var result = await _mediator.Send(new ReleaseWorkOrderCommand(id));
        return result.IsSuccess ? Ok() : BadRequest(result.Error);
    }
}
