using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// PERMISSION — A fine-grained action that can be performed in the system.
///
/// RBAC vs PERMISSION-BASED ACCESS (Interview Question!)
/// ======================================================
/// RBAC (Role-Based): "You are an Admin, so you can do everything."
///   → Simple, but not flexible enough for complex ERP scenarios.
///
/// Permission-Based: "You have the 'jobcard.create' permission."
///   → Fine-grained control. A role is just a GROUP of permissions.
///
/// OUR APPROACH: HYBRID
///   - Roles group users (Admin, Operator, QA)
///   - Permissions define actions (jobcard.create, workorder.approve)
///   - Roles have permissions (Admin → all permissions)
///   - Check happens at the permission level, not role level
///
/// PERMISSION NAMING CONVENTION:
/// ==============================
///   {module}.{action}
///   Examples:
///     "workorder.create"    — Create work orders
///     "workorder.view"      — View work orders
///     "jobcard.create"      — Create job cards
///     "jobcard.split"       — Split job cards
///     "qa.inspect"          — Perform QA inspection
///     "qa.approve"          — Approve QA results
///     "production.start"    — Start production
///     "user.manage"         — Manage users (admin)
/// </summary>
public class Permission : BaseEntity
{
    /// <summary>
    /// Permission code in format: module.action
    /// Example: "jobcard.create", "qa.approve"
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Human-readable name. Example: "Create Job Card"</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which module this permission belongs to. Example: "Production"</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>Navigation: roles that have this permission</summary>
    public ICollection<AppRolePermission> RolePermissions { get; set; } = [];
}
