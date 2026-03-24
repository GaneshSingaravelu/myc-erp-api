namespace ERP.Identity.Entities;

/// <summary>
/// ROLE-PERMISSION JUNCTION TABLE — Links roles to their permissions (many-to-many).
///
///   Roles ←→ AppRolePermission ←→ Permissions
///
///   Example:
///   ┌──────────┬──────────────────┐
///   │ RoleId   │ PermissionId     │
///   ├──────────┼──────────────────┤
///   │ role-qa  │ perm-jc-view     │  ← QA can view job cards
///   │ role-qa  │ perm-qa-inspect  │  ← QA can inspect
///   │ role-qa  │ perm-qa-approve  │  ← QA can approve
///   │ role-adm │ perm-jc-view     │  ← Admin can also view
///   │ role-adm │ perm-jc-create   │  ← Admin can create too
///   └──────────┴──────────────────┘
/// </summary>
public class AppRolePermission
{
    public Guid RoleId { get; set; }
    public AppRole Role { get; set; } = null!;

    public Guid PermissionId { get; set; }
    public Permission Permission { get; set; } = null!;
}
