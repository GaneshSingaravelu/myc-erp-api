using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// APPLICATION ROLE — Represents a role like "Admin", "QA Inspector", "Plant Manager".
///
/// RBAC: ROLE-BASED ACCESS CONTROL (Interview Question!)
/// =======================================================
/// RBAC controls WHAT a user can do based on their assigned roles.
///
/// Example:
///   User "John" has roles: ["Production Operator", "QA Inspector"]
///   - Production Operator CAN: Create JobCards, Start Production, Complete Production
///   - QA Inspector CAN: View JobCards, Perform QA Inspection, Approve/Reject
///   - Neither CAN: Delete WorkOrders (only Admin can)
///
/// HOW IT WORKS IN OUR SYSTEM:
/// ============================
///   AppUser ←→ AppUserRole ←→ AppRole ←→ AppRolePermission ←→ Permission
///
///   User "John"
///     → has role "QA Inspector"
///       → which has permissions: ["jobcard.view", "qa.inspect", "qa.approve"]
///
/// In the controller:
///   [Authorize(Roles = "QA Inspector")]  // Only QA Inspectors can access
///   public async Task&lt;IActionResult&gt; ApproveQa(...)
///
/// Or for fine-grained permission checks:
///   if (!_currentUser.HasPermission("qa.approve"))
///       return Forbid();
/// </summary>
public class AppRole : AuditableEntity
{
    /// <summary>Role name (e.g., "Admin", "QA Inspector", "Production Operator")</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable description of what this role does</summary>
    public string? Description { get; set; }

    /// <summary>Is this a system role that cannot be deleted? (e.g., "Admin")</summary>
    public bool IsSystemRole { get; set; }

    /// <summary>Navigation: users who have this role</summary>
    public ICollection<AppUserRole> UserRoles { get; set; } = [];

    /// <summary>Navigation: permissions granted to this role</summary>
    public ICollection<AppRolePermission> RolePermissions { get; set; } = [];
}
