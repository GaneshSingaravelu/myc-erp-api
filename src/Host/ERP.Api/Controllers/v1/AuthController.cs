using System.Security.Claims;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;

namespace ERP.Api.Controllers.v1;

/// <summary>
/// AUTH CONTROLLER — Login, Register, Refresh, Logout, Change Password.
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
    private readonly IWebHostEnvironment _env;

    public AuthController(IAuthService authService, IWebHostEnvironment env)
    {
        _authService = authService;
        _env = env;
    }

    /// <summary>
    /// POST /api/v1/auth/login
    /// Authenticates a user and returns JWT tokens + creates a session.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, [FromHeader(Name = "X-Tenant-Id")] string? tenantId)
    {
        // Require explicit tenant context — silent fallback ("default") hid client
        // configuration errors and risked routing into a real tenant whose code
        // happened to be "default". Forcing a 400 surfaces the misconfiguration.
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "X-Tenant-Id header is required." });

        var result = await _authService.LoginAsync(request, tenantId);

        if (result.IsFailure)
            return result.Error.Code switch
            {
                "Auth.InvalidCredentials" => Unauthorized(new { error = result.Error.Message }),
                "Auth.AccountLocked" => StatusCode(423, new { error = result.Error.Message }),
                "Auth.AccountInactive" => StatusCode(403, new { error = result.Error.Message }),
                _ => BadRequest(new { error = result.Error.Message })
            };

        return Ok(result.Value);
    }

    /// <summary>
    /// POST /api/v1/auth/register
    /// Creates a new user account (Admin only).
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
    /// Validates SecurityStamp — rejected if password/security changed.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request, [FromHeader(Name = "X-Tenant-Id")] string? tenantId)
    {
        // Same rule as /login — refusal is more honest than substituting "default".
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { error = "X-Tenant-Id header is required." });

        var result = await _authService.RefreshTokenAsync(request, tenantId);

        if (result.IsFailure)
            return Unauthorized(new { error = result.Error.Message });

        return Ok(result.Value);
    }

    /// <summary>
    /// POST /api/v1/auth/logout
    /// Revokes the current session (specific refresh token).
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        var result = await _authService.LogoutAsync(userId.Value, request.RefreshToken);
        return result.IsSuccess ? NoContent() : BadRequest(new { error = result.Error.Message });
    }

    /// <summary>
    /// POST /api/v1/auth/change-password
    /// Changes the user's password. Revokes all active sessions (force re-login).
    /// Checks password history (last 5 passwords cannot be reused).
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
            return Unauthorized();

        var result = await _authService.ChangePasswordAsync(userId.Value, request.CurrentPassword, request.NewPassword);

        if (result.IsFailure)
            return result.Error.Code switch
            {
                "Auth.InvalidPassword" => BadRequest(new { error = result.Error.Message }),
                "Auth.PasswordReused" => BadRequest(new { error = result.Error.Message }),
                _ => BadRequest(new { error = result.Error.Message })
            };

        return Ok(new { message = "Password changed successfully. All sessions have been revoked. Please log in again." });
    }

    /// <summary>
    /// POST /api/v1/auth/forgot-password
    /// Requests a password reset token for the given email.
    /// Always returns 200 (email enumeration prevention). In non-production environments
    /// the token is included in the response body for smoke testing.
    ///
    /// The token is bound to the X-Tenant-Id tenant (R2-DEV-02B-3A) and can only be redeemed
    /// through that same tenant. TenantMiddleware already requires the header on this route;
    /// a blank value still gets the generic 200 and nothing is issued.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request, [FromHeader(Name = "X-Tenant-Id")] string? tenantId)
    {
        var exposeToken = !_env.IsProduction();
        var result = await _authService.ForgotPasswordAsync(request, tenantId ?? string.Empty, exposeToken);
        return Ok(result.Value);
    }

    /// <summary>
    /// POST /api/v1/auth/reset-password
    /// Applies a new password using a previously issued reset token. The X-Tenant-Id must be
    /// the tenant the token was issued through.
    /// </summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request, [FromHeader(Name = "X-Tenant-Id")] string? tenantId)
    {
        var result = await _authService.ResetPasswordAsync(request, tenantId ?? string.Empty);

        if (result.IsFailure)
            return result.Error.Code switch
            {
                "Auth.ResetTokenExpired" => BadRequest(new { error = result.Error.Message }),
                "Auth.PasswordReused" => BadRequest(new { error = result.Error.Message }),
                _ => BadRequest(new { error = result.Error.Message })
            };

        return Ok(new { message = "Password reset successfully. Please log in with your new password." });
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
