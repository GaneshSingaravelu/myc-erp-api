using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// USER-ROLE JUNCTION TABLE — Links users to roles within a specific tenant context.
///
/// KEY DESIGN: Role assignments are PER-TENANT.
/// =============================================
/// The same user can have different roles in different tenants:
///   John → Tenant A (Admin) + Tenant B (Viewer)
///   Mary → Tenant A (Production Operator + QA Inspector)
///
/// Has its own Id (not composite key) for easier referencing, but enforces
/// uniqueness via (UserId, RoleId, TenantId) constraint.
/// </summary>
public class AppUserRole : BaseEntity
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;

    public Guid RoleId { get; set; }
    public AppRole Role { get; set; } = null!;

    /// <summary>Role assignment is per-tenant — same user, different roles per tenant</summary>
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>When was this role assigned?</summary>
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    /// <summary>User ID of who assigned this role (FK to Users)</summary>
    public Guid? AssignedBy { get; set; }

    /// <summary>Deactivate without removing the record</summary>
    public bool IsActive { get; set; } = true;
}
