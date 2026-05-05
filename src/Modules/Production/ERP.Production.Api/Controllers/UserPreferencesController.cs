using ERP.Production.Application.Commands;
using ERP.Production.Application.Queries;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Production.Api.Controllers;

/// <summary>
/// USER PREFERENCES API — Per-user key-value settings stored in the tenant DB.
///
/// GET  /api/v1/preferences          → All preferences for current user
/// GET  /api/v1/preferences/{key}    → Single preference by key
/// PUT  /api/v1/preferences/{key}    → Create or update a preference
/// </summary>
[ApiController]
[Route("api/v1/preferences")]
[Authorize]
public class UserPreferencesController : ControllerBase
{
    private readonly IMediator _mediator;

    public UserPreferencesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var result = await _mediator.Send(new GetAllUserPreferencesQuery());
        return result.IsSuccess ? Ok(result.Value) : BadRequest(result.Error);
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key)
    {
        var result = await _mediator.Send(new GetUserPreferenceQuery(key));
        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Set(string key, [FromBody] SetPreferenceRequest request)
    {
        var result = await _mediator.Send(new SetUserPreferenceCommand(key, request.Value));
        return result.IsSuccess ? NoContent() : BadRequest(result.Error);
    }
}

public record SetPreferenceRequest(string Value);
