using System.Security.Claims;
using ERP.Application.Abstractions.Identity;
using Microsoft.AspNetCore.Http;

namespace ERP.Infrastructure.Tenant;

/// <summary>
/// CURRENT USER SERVICE — Reads user info from the JWT token in the current request.
///
/// WHAT IS A JWT CLAIM? (Interview Question!)
/// ============================================
/// When a user logs in, the server creates a JWT token containing "claims":
///   {
///     "sub": "user-id-123",           ← ClaimTypes.NameIdentifier (UserId)
///     "email": "john@abc.com",        ← ClaimTypes.Email
///     "role": ["Admin", "QA"],        ← ClaimTypes.Role
///     "tenant_id": "abc-manufacturing" ← Custom claim
///   }
///
/// The Angular frontend sends this token in every request:
///   Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
///
/// ASP.NET Core's JWT middleware validates and parses the token,
/// making claims available via HttpContext.User.Claims.
///
/// This service provides a clean way to access those claims.
///
/// REGISTERED AS: Scoped (one instance per HTTP request)
/// </summary>
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Gets the HttpContext.User (the ClaimsPrincipal from the JWT token).
    /// Returns null if there's no HTTP context (background services, etc.)
    /// </summary>
    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public string? UserId => User?.FindFirstValue(ClaimTypes.NameIdentifier);

    public string? Email => User?.FindFirstValue(ClaimTypes.Email);

    public string? TenantId => User?.FindFirstValue("tenant_id");

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public bool IsInRole(string role) => User?.IsInRole(role) ?? false;
}
