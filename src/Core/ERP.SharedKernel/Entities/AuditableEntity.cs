namespace ERP.SharedKernel.Entities;

/// <summary>
/// AUDITABLE ENTITY — Adds "who created/modified this and when?" tracking.
///
/// WHY AUDIT FIELDS?
/// ==================
/// In ERP systems, you ALWAYS need to know:
/// - Who created a record? (CreatedBy)
/// - When was it created? (CreatedAt)
/// - Who last modified it? (LastModifiedBy)
/// - When was it last modified? (LastModifiedAt)
/// - Is it deleted? (IsDeleted) — Soft delete pattern
///
/// DESIGN PATTERN: Soft Delete
/// ============================
/// Instead of actually deleting records from the database (hard delete),
/// we set IsDeleted = true. This way:
/// - Data is never lost (audit trail, compliance)
/// - Related records don't break (foreign key integrity)
/// - You can "undelete" records if needed
/// - All queries automatically filter out deleted records (via EF Core global query filter)
///
/// INTERVIEW TIP: "We use soft delete with a global query filter in EF Core
/// to automatically exclude deleted records from all queries."
/// </summary>
public abstract class AuditableEntity : BaseEntity
{
    /// <summary>When this record was first created (UTC)</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>User ID or username of who created this record</summary>
    public string? CreatedBy { get; set; }

    /// <summary>When this record was last modified (UTC)</summary>
    public DateTime? LastModifiedAt { get; set; }

    /// <summary>User ID or username of who last modified this record</summary>
    public string? LastModifiedBy { get; set; }

    /// <summary>
    /// Soft delete flag. When true, this record is "deleted" but still in the database.
    /// EF Core global query filter automatically excludes these from queries.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>When this record was soft-deleted (UTC)</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>User ID or username of who deleted this record</summary>
    public string? DeletedBy { get; set; }
}
