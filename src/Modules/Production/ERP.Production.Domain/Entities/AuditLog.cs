using ERP.SharedKernel.Entities;

namespace ERP.Production.Domain.Entities;

/// <summary>
/// AUDIT LOG — Audit trail for business data changes within the tenant.
///
/// Stored in the Tenant DB alongside the business data it describes.
/// Identity audit logs are stored separately in Master DB (IdentityAuditLog).
///
/// ACTIONS TRACKED:
/// =================
///   Create, Update, Delete, StatusChange, Approve, Reject,
///   Release, Complete, Cancel, Split, Rework
///
/// Populated automatically via EF Core SaveChanges interceptor.
/// </summary>
public class AuditLog : BaseEntity
{
    /// <summary>References Master DB Users.Id (no FK — cross-database reference)</summary>
    public Guid? UserId { get; set; }

    /// <summary>Action performed (e.g., "Create", "StatusChange", "Approve"). Max 50.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Entity type (e.g., "WorkOrder", "JobCard", "Plant"). Max 200.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>ID of affected entity (NVARCHAR for flexibility with composite keys). Max 100.</summary>
    public string? EntityId { get; set; }

    /// <summary>JSON snapshot of entity state before the change</summary>
    public string? OldValues { get; set; }

    /// <summary>JSON snapshot of entity state after the change</summary>
    public string? NewValues { get; set; }

    /// <summary>Client IP address</summary>
    public string? IpAddress { get; set; }

    /// <summary>Browser/device info from User-Agent header</summary>
    public string? UserAgent { get; set; }

    /// <summary>When this audit event occurred</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
