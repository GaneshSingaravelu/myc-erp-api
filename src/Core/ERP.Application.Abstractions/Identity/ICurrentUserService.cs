namespace ERP.Application.Abstractions.Identity;

/// <summary>
/// CURRENT USER SERVICE — Provides information about the logged-in user.
///
/// WHY AN INTERFACE HERE?
/// =======================
/// The Application layer needs to know WHO is performing an action (for audit trails,
/// permission checks, etc.), but it can't directly access HttpContext (that's ASP.NET).
///
/// So we define this interface HERE (in Application.Abstractions) and implement it
/// in ERP.Infrastructure where HttpContext is available.
///
/// This is the DEPENDENCY INVERSION PRINCIPLE (Interview Question!):
/// - High-level module (Application) defines the interface
/// - Low-level module (Infrastructure) provides the implementation
/// - Both depend on the abstraction (this interface)
///
/// USAGE:
/// ======
/// // In AuditableEntityInterceptor (sets CreatedBy/ModifiedBy):
/// var userId = _currentUser.UserId;
///
/// // In a command handler (check permissions):
/// if (!_currentUser.IsInRole("QA_Inspector"))
///     return Result.Failure(Error.Unauthorized());
/// </summary>
public interface ICurrentUserService
{
    /// <summary>The current user's ID (from JWT token claim "sub")</summary>
    string? UserId { get; }

    /// <summary>The current user's email (from JWT token claim "email")</summary>
    string? Email { get; }

    /// <summary>The current tenant ID (from JWT token or header)</summary>
    string? TenantId { get; }

    /// <summary>Check if the current user is authenticated</summary>
    bool IsAuthenticated { get; }

    /// <summary>Check if the current user has a specific role</summary>
    bool IsInRole(string role);
}
