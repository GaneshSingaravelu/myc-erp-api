using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// ROLE-PERMISSION JUNCTION TABLE — Maps permissions to roles.
///
/// ADVANCED FEATURES:
/// ===================
/// - IsAllowed: true = grant, false = explicit deny (deny overrides grant)
/// - ConditionJson: ABAC conditions for data-level filtering (future use)
/// - ValidFrom/ValidTo: Time-bound permissions (e.g., contractor access for 30 days)
///
/// ABAC ConditionJson Examples (NOT implemented now, schema ready):
///   { "PlantId": "User.PlantId" }     → Only access own plant's data
///   { "Department": "QA" }             → Only access QA department data
///   { "MaxAmount": 100000 }            → Approval limit
///   { "CreatedBy": "User.Id" }         → Only access own records
/// </summary>
public class AppRolePermission : BaseEntity
{
    public Guid RoleId { get; set; }
    public AppRole Role { get; set; } = null!;

    public Guid PermissionId { get; set; }
    public Permission Permission { get; set; } = null!;

    /// <summary>true = grant permission, false = explicitly deny (deny wins over grant)</summary>
    public bool IsAllowed { get; set; } = true;

    /// <summary>ABAC conditions as JSON (future use). NULL = no conditions.</summary>
    public string? ConditionJson { get; set; }

    /// <summary>Permission starts at this time (NULL = immediately)</summary>
    public DateTime? ValidFrom { get; set; }

    /// <summary>Permission expires at this time (NULL = never expires)</summary>
    public DateTime? ValidTo { get; set; }

    /// <summary>When this mapping was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
