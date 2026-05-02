namespace ERP.SharedKernel.Entities;

/// <summary>
/// Audit trail for business data changes within the tenant DB.
/// Cross-cutting: tracks changes across ALL modules (Production, Workflow, etc.).
/// Identity audit logs are stored separately in Master DB (IdentityAuditLog).
/// </summary>
public class AuditLog : BaseEntity
{
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
