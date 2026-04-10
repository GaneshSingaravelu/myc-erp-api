using ERP.SharedKernel.Entities;

namespace ERP.Identity.Entities;

/// <summary>
/// MENU — Navigation items within modules. Supports unlimited hierarchy via ParentMenuId.
///
/// API-DRIVEN DYNAMIC SIDEBAR:
/// ============================
/// After login, Angular calls GET /api/v1/menus/my-menus which:
///   1. Gets user's permissions for current tenant
///   2. Filters menus by permissions (via MenuPermissions)
///   3. Checks feature flags for the tenant
///   4. Returns the filtered menu tree
///
/// Angular builds the sidebar dynamically — NO hardcoded navigation.
///
/// HIERARCHY EXAMPLE:
/// ==================
///   Module: Production
///     ├── Work Orders        (ParentMenuId = null)
///     │     ├── Create WO    (ParentMenuId = Work Orders)
///     │     └── WO List      (ParentMenuId = Work Orders)
///     └── Job Cards          (ParentMenuId = null)
///           └── JC Dashboard (ParentMenuId = Job Cards)
/// </summary>
public class Menu : BaseEntity
{
    /// <summary>Parent module this menu belongs to</summary>
    public Guid ModuleId { get; set; }
    public Module Module { get; set; } = null!;

    /// <summary>Machine code (e.g., "MENU_WO_LIST", "MENU_JC_DASHBOARD"). Max 100.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name in the sidebar (e.g., "Job Cards"). Max 200.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>NULL = top-level within module; GUID = child of another menu</summary>
    public Guid? ParentMenuId { get; set; }
    public Menu? ParentMenu { get; set; }

    /// <summary>Angular route (e.g., "/production/job-cards"). NULL for parent-only menus.</summary>
    public string? Route { get; set; }

    /// <summary>Angular component name (e.g., "JobCardListComponent"). NULL if route-based.</summary>
    public string? ComponentName { get; set; }

    /// <summary>Menu item icon (e.g., "lucide-clipboard-list"). Max 100.</summary>
    public string? Icon { get; set; }

    /// <summary>Sort order within parent</summary>
    public int DisplayOrder { get; set; }

    /// <summary>Show in navigation (false = hidden but route still works)</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Visible but greyed out if false (coming soon, etc.)</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// References FeatureFlags.Code. NULL = always shown.
    /// Menu is hidden if the feature flag is disabled for the tenant.
    /// </summary>
    public string? FeatureFlag { get; set; }

    /// <summary>When this menu was created</summary>
    public DateTime CreatedAt { get; set; }

    // ── Navigation Properties ──

    /// <summary>Child menus (for hierarchy)</summary>
    public ICollection<Menu> ChildMenus { get; set; } = [];

    /// <summary>Permissions required to see this menu</summary>
    public ICollection<MenuPermission> MenuPermissions { get; set; } = [];
}
