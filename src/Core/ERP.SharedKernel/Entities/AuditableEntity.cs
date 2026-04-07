namespace ERP.SharedKernel.Entities;

/// <summary>
/// AUDITABLE ENTITY — Adds audit tracking, soft delete, and business-level active flag.
///
/// HYBRID SOFT DELETE STRATEGY (ERP Requirement):
/// ================================================
/// Two independent flags serve different purposes:
///
///   IsActive = false → "Account suspended" (reversible by admin)
///   IsDeleted = true → "Logically removed" (hidden from all queries)
///
/// Example:
///   Employee leaves company → IsActive = false (account disabled, record visible in reports)
///   GDPR data removal request → IsDeleted = true (hidden from all queries, audit preserved)
///
/// EF Core global query filter automatically excludes IsDeleted=true from all queries.
/// To include deleted records: _context.Users.IgnoreQueryFilters().ToListAsync()
///
/// DESIGN PATTERN: Layer Supertype + Soft Delete
/// </summary>
public abstract class AuditableEntity : BaseEntity
{
    /// <summary>Business-level enable/disable. Inactive records are visible but cannot be used.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When this record was first created (UTC)</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>User ID of who created this record (FK to Users in Master DB)</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>When this record was last modified (UTC)</summary>
    public DateTime? LastModifiedAt { get; set; }

    /// <summary>User ID of who last modified this record (FK to Users in Master DB)</summary>
    public Guid? LastModifiedBy { get; set; }

    /// <summary>
    /// Soft delete flag. When true, this record is "deleted" but still in the database.
    /// EF Core global query filter automatically excludes these from queries.
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>When this record was soft-deleted (UTC)</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>User ID of who deleted this record (FK to Users in Master DB)</summary>
    public Guid? DeletedBy { get; set; }
}
