using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// MENU-PERMISSION JUNCTION — Links menus to required permissions.
///
/// A menu is visible only if the user has the required permissions.
///
/// LOGIC:
/// =======
///   IsRequired = true for ALL linked permissions → user needs ALL (AND logic)
///   If menu has NO MenuPermissions → visible to all authenticated users
///
/// Example:
///   "Create Work Order" menu requires:
///     - WORKORDER_CREATE (IsRequired = true)
///   "QA Dashboard" menu requires:
///     - QA_VIEW (IsRequired = true)
///     - QA_APPROVE (IsRequired = false) → optional, shows extra features if present
/// </summary>
public class MenuPermission : BaseEntity
{
    public Guid MenuId { get; set; }
    public Menu Menu { get; set; } = null!;

    public Guid PermissionId { get; set; }
    public Permission Permission { get; set; } = null!;

    /// <summary>true = must have this permission to see menu</summary>
    public bool IsRequired { get; set; } = true;
}
