using ERP.Workflow.Application.Commands;
using ERP.Workflow.Application.DTOs;
using ERP.Workflow.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Workflow.Api.Controllers;

/// <summary>
/// PROCESS TEMPLATES API — CRUD endpoints for managing production workflows.
///
/// CONTROLLER RESPONSIBILITIES (Interview Question!):
/// ====================================================
/// A controller should ONLY do:
/// 1. Receive the HTTP request
/// 2. Map it to a Command/Query
/// 3. Send via MediatR
/// 4. Map the Result to an HTTP response
///
/// It should NOT contain business logic. That lives in the Handler.
/// This is called the "Thin Controller" pattern.
///
/// API ENDPOINTS:
/// ==============
/// GET    /api/v1/workflow/process-templates           → List all templates
/// GET    /api/v1/workflow/process-templates/{id}      → Get template with steps
/// POST   /api/v1/workflow/process-templates           → Create new template
/// PUT    /api/v1/workflow/process-templates/{id}      → Update template
/// DELETE /api/v1/workflow/process-templates/{id}      → Soft-delete template
///
/// POST   /api/v1/workflow/process-templates/{id}/steps       → Add step
/// PUT    /api/v1/workflow/process-templates/{id}/steps/{sid} → Update step
/// DELETE /api/v1/workflow/process-templates/{id}/steps/{sid} → Remove step
/// </summary>
[ApiController]
[Route("api/v1/workflow/process-templates")]
[Authorize]
public class ProcessTemplatesController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProcessTemplatesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    // ── TEMPLATE ENDPOINTS ──────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool includeInactive = false)
    {
        var result = await _mediator.Send(new GetProcessTemplatesQuery(includeInactive));
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await _mediator.Send(new GetProcessTemplateByIdQuery(id));
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProcessTemplateRequest request)
    {
        var command = new CreateProcessTemplateCommand(
            request.Name, request.Description, request.Steps);

        var result = await _mediator.Send(command);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value)
            : BadRequest(result.Error);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProcessTemplateRequest request)
    {
        var command = new UpdateProcessTemplateCommand(id, request.Name, request.Description);
        var result = await _mediator.Send(command);
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _mediator.Send(new DeleteProcessTemplateCommand(id));
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }

    // ── STEP ENDPOINTS ──────────────────────────────────────────

    [HttpPost("{templateId:guid}/steps")]
    public async Task<IActionResult> AddStep(Guid templateId, [FromBody] CreateProcessStepRequest request)
    {
        var command = new AddProcessStepCommand(
            templateId,
            request.DepartmentName,
            request.Order,
            request.StepType,
            request.QaRequired,
            request.Instructions,
            request.NextStepId,
            request.EstimatedDurationMinutes);

        var result = await _mediator.Send(command);
        return result.IsSuccess ? Ok(new { stepId = result.Value }) : BadRequest(result.Error);
    }

    [HttpPut("{templateId:guid}/steps/{stepId:guid}")]
    public async Task<IActionResult> UpdateStep(
        Guid templateId, Guid stepId, [FromBody] UpdateProcessStepRequest request)
    {
        var command = new UpdateProcessStepCommand(
            templateId, stepId,
            request.DepartmentName,
            request.Order,
            request.StepType,
            request.QaRequired,
            request.Instructions,
            request.NextStepId,
            request.EstimatedDurationMinutes);

        var result = await _mediator.Send(command);
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }

    [HttpDelete("{templateId:guid}/steps/{stepId:guid}")]
    public async Task<IActionResult> RemoveStep(Guid templateId, Guid stepId)
    {
        var result = await _mediator.Send(new RemoveProcessStepCommand(templateId, stepId));
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }
}
