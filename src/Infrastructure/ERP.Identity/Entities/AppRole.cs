using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// APPLICATION ROLE — Defines user roles like "Admin", "QA Inspector", "Plant Manager".
///
/// SYSTEM ROLES vs TENANT ROLES:
/// ===============================
///   TenantId = NULL  → System role (Admin, Viewer) — available to ALL tenants
///   TenantId = GUID  → Tenant-specific role (custom) — only for that tenant
///
/// The 4 seeded system roles (TenantId = NULL):
///   Admin, Production Operator, QA Inspector, Viewer
///
/// Tenants can create custom roles like "Chennai Floor Manager" with their TenantId.
///
/// RBAC + PERMISSION-BASED ACCESS:
/// ================================
///   User → UserRoles → Roles → RolePermissions → Permissions
///   Authorization checks happen at the PERMISSION level, not role level.
/// </summary>
public class AppRole : AuditableEntity
{
    /// <summary>Role name (e.g., "Admin", "QA Inspector")</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Uppercase copy for case-insensitive lookup (auto-computed)</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>Human-readable description of what this role does</summary>
    public string? Description { get; set; }

    /// <summary>System roles cannot be deleted/modified by tenants</summary>
    public bool IsSystemRole { get; set; }

    /// <summary>
    /// NULL = system role (available to all tenants).
    /// GUID = tenant-specific custom role.
    /// </summary>
    public Guid? TenantId { get; set; }

    // ── Navigation Properties ──

    /// <summary>Tenant this role belongs to (null for system roles)</summary>
    public Tenant? Tenant { get; set; }

    /// <summary>Users who have this role</summary>
    public ICollection<AppUserRole> UserRoles { get; set; } = [];

    /// <summary>Permissions granted to this role</summary>
    public ICollection<AppRolePermission> RolePermissions { get; set; } = [];
}
