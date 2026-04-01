using ERP.Production.Application.Commands;
using ERP.Production.Application.DTOs;
using ERP.Production.Application.Queries;
using ERP.Production.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Production.Api.Controllers;

/// <summary>
/// JOB CARDS API — Endpoints for managing job cards on the shop floor.
///
/// API ENDPOINTS:
/// ==============
/// GET  /api/v1/production/job-cards                    → List job cards (filterable)
/// PUT  /api/v1/production/job-cards/{id}/status        → Update job card status (action-based)
///
/// STATUS ACTIONS:
/// ===============
/// "start"         → Begin working on the job card
/// "complete-step" → Mark current step as done
/// "pass-qa"       → QA approves → moves to next step or completes
/// "fail-qa"       → QA rejects → creates rework job card
/// "hold"          → Put on hold
/// "resume"        → Resume from hold
/// "cancel"        → Cancel the job card
/// </summary>
[ApiController]
[Route("api/v1/production/job-cards")]
[Authorize]
public class JobCardsController : ControllerBase
{
    private readonly IMediator _mediator;

    public JobCardsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] Guid? workOrderId = null,
        [FromQuery] JobCardStatus? status = null)
    {
        var result = await _mediator.Send(new GetJobCardsQuery(workOrderId, status));
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> UpdateStatus(
        Guid id, [FromBody] UpdateJobCardStatusRequest request)
    {
        var command = new UpdateJobCardStatusCommand(id, request.Action, request.Notes);
        var result = await _mediator.Send(command);
        return result.IsSuccess ? Ok() : BadRequest(result.Error);
    }
}
