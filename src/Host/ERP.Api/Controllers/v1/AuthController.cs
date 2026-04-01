using ERP.Identity.DTOs;
using ERP.Identity.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers.v1;

/// <summary>
/// AUTH CONTROLLER — Login, Register, Refresh Token endpoints.
///
/// CONTROLLER DESIGN (Interview Concept):
/// ========================================
/// Controllers should be THIN — they only:
/// 1. Accept the HTTP request
/// 2. Call a service/mediator
/// 3. Map the result to an HTTP response
///
/// NO business logic in controllers. All logic lives in services/handlers.
///
/// ROUTE CONVENTION: /api/v1/{resource}
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// POST /api/v1/auth/login
    /// Authenticates a user and returns JWT tokens.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, [FromHeader(Name = "X-Tenant-Id")] string? tenantId)
    {
        var result = await _authService.LoginAsync(request, tenantId ?? "default");

        if (result.IsFailure)
            return result.Error.Code == "Auth.InvalidCredentials"
                ? Unauthorized(new { error = result.Error.Message })
                : BadRequest(new { error = result.Error.Message });

        return Ok(result.Value);
    }

    /// <summary>
    /// POST /api/v1/auth/register
    /// Creates a new user account.
    /// </summary>
    [HttpPost("register")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await _authService.RegisterAsync(request);

        if (result.IsFailure)
            return Conflict(new { error = result.Error.Message });

        return CreatedAtAction(nameof(Register), new { id = result.Value.Id }, result.Value);
    }

    /// <summary>
    /// POST /api/v1/auth/refresh
    /// Exchange expired access token + valid refresh token for new tokens.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request, [FromHeader(Name = "X-Tenant-Id")] string? tenantId)
    {
        var result = await _authService.RefreshTokenAsync(request, tenantId ?? "default");

        if (result.IsFailure)
            return Unauthorized(new { error = result.Error.Message });

        return Ok(result.Value);
    }
}
