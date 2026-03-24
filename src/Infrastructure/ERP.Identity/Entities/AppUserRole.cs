namespace ERP.Identity.Entities;

/// <summary>
/// USER-ROLE JUNCTION TABLE — Links users to their roles (many-to-many).
///
/// WHAT IS A MANY-TO-MANY RELATIONSHIP?
/// ======================================
/// A user can have MANY roles (Admin + QA Inspector).
/// A role can belong to MANY users (10 people are QA Inspectors).
///
/// In relational databases, many-to-many requires a "junction table":
///   Users ←→ AppUserRole ←→ Roles
///
///   AppUserRole table:
///   ┌──────────┬──────────┐
///   │ UserId   │ RoleId   │
///   ├──────────┼──────────┤
///   │ user-001 │ role-adm │  ← John is Admin
///   │ user-001 │ role-qa  │  ← John is also QA Inspector
///   │ user-002 │ role-qa  │  ← Jane is QA Inspector
///   └──────────┴──────────┘
///
/// EF Core can auto-create this table, but we create it explicitly
/// to add extra fields (AssignedAt, AssignedBy) for audit purposes.
/// </summary>
public class AppUserRole
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    public Guid RoleId { get; set; }
    public AppRole Role { get; set; } = null!;

    /// <summary>When was this role assigned?</summary>
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who assigned this role?</summary>
    public string? AssignedBy { get; set; }
}
