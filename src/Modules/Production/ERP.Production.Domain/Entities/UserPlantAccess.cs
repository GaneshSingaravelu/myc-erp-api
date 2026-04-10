using ERP.SharedKernel.Entities;

namespace ERP.Production.Domain.Entities;

/// <summary>
/// USER PLANT ACCESS — Controls which plants a user can access within this tenant.
///
/// Stored in Tenant DB. UserId references Master DB Users (no FK constraint — cross-DB).
/// Validated in application code instead.
///
/// ACCESS LEVELS:
/// ===============
///   VIEW    → Read-only access (viewer seeing Chennai data)
///   OPERATE → Create/update own work (worker creating job cards)
///   MANAGE  → Full CRUD + assign work to others (floor supervisor)
///   APPROVE → Approve/reject + all manage rights (plant manager)
/// </summary>
public class UserPlantAccess : BaseEntity
{
    /// <summary>References Master DB Users.Id (no FK — cross-database reference)</summary>
    public Guid UserId { get; set; }

    /// <summary>Which plant</summary>
    public Guid PlantId { get; set; }
    public Plant Plant { get; set; } = null!;

    /// <summary>Access level: VIEW, OPERATE, MANAGE, APPROVE</summary>
    public string AccessLevel { get; set; } = "VIEW";

    /// <summary>Default plant for this user in this tenant</summary>
    public bool IsDefault { get; set; }

    /// <summary>Active/inactive access</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When access was granted</summary>
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who granted access (references Master DB Users.Id, no FK)</summary>
    public Guid? GrantedBy { get; set; }
}
