using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// PERMISSION — A fine-grained action that can be performed in the system.
///
/// Permissions are system-defined (seeded), NOT tenant-created.
/// They are immutable reference data — no AuditableEntity needed.
///
/// PERMISSION NAMING CONVENTION:
/// ==============================
///   CATEGORY_ACTION (uppercase with underscore)
///   Examples:
///     "WORKORDER_CREATE"   — Create work orders
///     "JOBCARD_VIEW"       — View job cards
///     "QA_APPROVE"         — Approve QA results
///     "PRODUCTION_MANAGE"  — Manage production data
///     "USER_CREATE"        — Create users (admin)
///     "TENANT_MANAGE"      — Manage tenant settings
///
/// HYBRID ACCESS CONTROL:
/// =======================
///   RBAC: Role-based checks ([Authorize(Roles = "Admin")])
///   Permission-based: Fine-grained checks (HasPermission("QA_APPROVE"))
///   ABAC (future): RolePermissions.ConditionJson for data-level filtering
/// </summary>
public class Permission : BaseEntity
{
    /// <summary>Permission code: CATEGORY_ACTION (e.g., "JOBCARD_CREATE", "QA_APPROVE")</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable name. Example: "Create Job Card"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Grouping for admin UI (e.g., "Production", "Quality", "Admin")</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Detailed explanation of what this permission allows</summary>
    public string? Description { get; set; }

    // ── Navigation Properties ──

    /// <summary>Roles that have this permission</summary>
    public ICollection<AppRolePermission> RolePermissions { get; set; } = [];

    /// <summary>Menus that require this permission</summary>
    public ICollection<MenuPermission> MenuPermissions { get; set; } = [];
}
