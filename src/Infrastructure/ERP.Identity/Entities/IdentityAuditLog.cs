using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// IDENTITY AUDIT LOG — Audit trail for authentication and identity-related actions.
///
/// Stored in Master DB alongside identity data.
/// Business data audit logs are stored separately in each Tenant DB.
///
/// ACTIONS TRACKED:
/// =================
///   Login, LoginFailed, Logout, PasswordChanged, RoleAssigned,
///   PermissionDenied, AccountLocked, AccountUnlocked, SessionRevoked,
///   TenantCreated, UserCreated, UserDeactivated
/// </summary>
public class IdentityAuditLog : BaseEntity
{
    /// <summary>NULL for global actions (e.g., failed login with unknown tenant)</summary>
    public Guid? TenantId { get; set; }

    /// <summary>NULL for failed logins with unknown user</summary>
    public Guid? UserId { get; set; }

    /// <summary>Action performed (e.g., "Login", "LoginFailed", "PasswordChanged"). Max 50.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Entity type affected (e.g., "User", "Role", "Permission"). Max 200.</summary>
    public string? EntityType { get; set; }

    /// <summary>ID of the affected entity (NVARCHAR for flexibility). Max 100.</summary>
    public string? EntityId { get; set; }

    /// <summary>Additional context as JSON (e.g., old/new values, error message). Max.</summary>
    public string? Details { get; set; }

    /// <summary>Client IP address</summary>
    public string? IpAddress { get; set; }

    /// <summary>Browser/device info from User-Agent header</summary>
    public string? UserAgent { get; set; }

    /// <summary>When this audit event occurred</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
