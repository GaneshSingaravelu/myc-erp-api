using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.SharedKernel.Results;

namespace ERP.Identity.Services;

/// <summary>
/// MENU SERVICE — Builds the permission-filtered sidebar menu tree.
///
/// FLOW:
/// ======
/// 1. Load user's permission codes (via roles for the given tenant)
/// 2. Load all active modules + visible menus + menu permissions
/// 3. Resolve feature flags (global defaults + tenant overrides)
/// 4. Filter: keep menus where user has required permissions AND feature flag is enabled
/// 5. Build hierarchical tree (modules → top-level menus → children)
///
/// Angular calls this once after login and caches the result in the store.
///
/// Persistence is read-only and delegated entirely to repositories — the service
/// has no direct EF Core or MasterDbContext reference (enforced by architecture test).
/// </summary>
public class MenuService : IMenuService
{
    private readonly IUserRepository _users;
    private readonly IMenuRepository _menus;
    private readonly IPermissionRepository _permissions;
    private readonly IFeatureFlagRepository _featureFlags;

    public MenuService(
        IUserRepository users,
        IMenuRepository menus,
        IPermissionRepository permissions,
        IFeatureFlagRepository featureFlags)
    {
        _users = users;
        _menus = menus;
        _permissions = permissions;
        _featureFlags = featureFlags;
    }

    public async Task<Result<IReadOnlyList<ModuleMenuResponse>>> GetMyMenusAsync(Guid userId, Guid? tenantId)
    {
        // Step 1: Get user's permission codes (distinct across all active roles)
        var userPermissions = await _users.GetPermissionCodesForUserAsync(userId);
        var permissionSet = userPermissions.ToHashSet();

        // Step 2: Resolve feature flags — tenant overrides first, then global defaults
        var tenantFlags = tenantId.HasValue
            ? await _featureFlags.GetTenantOverridesAsync(tenantId.Value)
            : new Dictionary<string, bool>();

        var globalFlags = await _featureFlags.GetGlobalFlagsAsync();

        // Step 3: Load all active modules with their visible menus and menu permissions
        var modules = await _menus.GetActiveModulesWithVisibleMenusAsync();

        // Step 4: Filter and build response
        var result = new List<ModuleMenuResponse>();

        foreach (var module in modules)
        {
            var filteredMenus = FilterMenus(module.Menus, permissionSet, tenantFlags, globalFlags);
            if (filteredMenus.Count == 0)
                continue; // Skip modules with no visible menus for this user

            result.Add(new ModuleMenuResponse(
                Id: module.Id,
                Code: module.Code,
                Name: module.Name,
                Icon: module.Icon,
                DisplayOrder: module.DisplayOrder,
                Menus: filteredMenus));
        }

        return Result.Success<IReadOnlyList<ModuleMenuResponse>>(result);
    }

    public async Task<Result<IReadOnlyList<PermissionTreeNode>>> GetPermissionTreeAsync()
    {
        // Step 1: Load all modules + menus + their MenuPermissions → Permission
        var modules = await _menus.GetActiveModulesWithVisibleMenusAsync();

        // Step 2: Load ALL permissions (some may not be linked to any menu)
        var allPermissions = await _permissions.GetAllOrderedAsync();
        var linkedPermissionIds = new HashSet<Guid>();

        // Step 3: Build tree: Module → Menu → Permissions
        var tree = new List<PermissionTreeNode>();

        foreach (var module in modules)
        {
            var menuNodes = BuildMenuPermissionNodes(module.Menus.ToList(), null, linkedPermissionIds);
            if (menuNodes.Count > 0)
            {
                tree.Add(new PermissionTreeNode(
                    Id: $"module_{module.Id}",
                    Label: module.Name,
                    PermissionId: null,
                    Children: menuNodes));
            }
        }

        // Step 4: Collect unlinked permissions under "Other" node
        var unlinked = allPermissions
            .Where(p => !linkedPermissionIds.Contains(p.Id))
            .Select(p => new PermissionTreeNode(
                Id: $"perm_{p.Id}",
                Label: $"{p.Name} ({p.Code})",
                PermissionId: p.Id.ToString(),
                Children: new List<PermissionTreeNode>()))
            .ToList();

        if (unlinked.Count > 0)
        {
            tree.Add(new PermissionTreeNode(
                Id: "module_other",
                Label: "Other",
                PermissionId: null,
                Children: unlinked));
        }

        return Result.Success<IReadOnlyList<PermissionTreeNode>>(tree);
    }

    private static List<PermissionTreeNode> BuildMenuPermissionNodes(
        List<Menu> allMenus,
        Guid? parentId,
        HashSet<Guid> linkedPermissionIds)
    {
        var nodes = new List<PermissionTreeNode>();

        var children = allMenus
            .Where(m => m.ParentMenuId == parentId)
            .OrderBy(m => m.DisplayOrder)
            .ToList();

        foreach (var menu in children)
        {
            // Recurse into child menus
            var childNodes = BuildMenuPermissionNodes(allMenus, menu.Id, linkedPermissionIds);

            // Add this menu's own permissions as leaf nodes (skip duplicates —
            // the same permission can be linked to multiple menus via MenuPermission;
            // showing it once avoids confusing toggle behavior in the UI)
            foreach (var mp in menu.MenuPermissions.OrderBy(mp => mp.Permission.Code))
            {
                if (linkedPermissionIds.Add(mp.Permission.Id)) // returns false if already present
                {
                    childNodes.Add(new PermissionTreeNode(
                        Id: $"perm_{mp.Permission.Id}",
                        Label: $"{mp.Permission.Name} ({mp.Permission.Code})",
                        PermissionId: mp.Permission.Id.ToString(),
                        Children: new List<PermissionTreeNode>()));
                }
            }

            if (childNodes.Count > 0)
            {
                nodes.Add(new PermissionTreeNode(
                    Id: $"menu_{menu.Id}",
                    Label: menu.Name,
                    PermissionId: null,
                    Children: childNodes));
            }
        }

        return nodes;
    }

    /// <summary>
    /// Filter menus by permissions and feature flags, then build hierarchical tree.
    /// </summary>
    private static List<MenuItemResponse> FilterMenus(
        ICollection<Menu> allMenus,
        HashSet<string> userPermissions,
        IReadOnlyDictionary<string, bool> tenantFlags,
        IReadOnlyDictionary<string, bool> globalFlags)
    {
        // First, determine which menus the user can see
        var visibleMenus = allMenus
            .Where(m => IsMenuVisible(m, userPermissions, tenantFlags, globalFlags))
            .ToList();

        // Build tree: top-level menus (ParentMenuId = null) with children
        return visibleMenus
            .Where(m => m.ParentMenuId is null)
            .OrderBy(m => m.DisplayOrder)
            .Select(m => BuildMenuItem(m, visibleMenus))
            .ToList();
    }

    private static bool IsMenuVisible(
        Menu menu,
        HashSet<string> userPermissions,
        IReadOnlyDictionary<string, bool> tenantFlags,
        IReadOnlyDictionary<string, bool> globalFlags)
    {
        // Check feature flag (if menu has one)
        if (!string.IsNullOrEmpty(menu.FeatureFlag))
        {
            // Tenant override takes precedence, then global default
            var flagEnabled = tenantFlags.TryGetValue(menu.FeatureFlag, out var tenantOverride)
                ? tenantOverride
                : globalFlags.TryGetValue(menu.FeatureFlag, out var globalDefault) && globalDefault;

            if (!flagEnabled)
                return false;
        }

        // Check permissions: if menu has required permissions, user must have ALL of them
        var requiredPermissions = menu.MenuPermissions
            .Where(mp => mp.IsRequired)
            .Select(mp => mp.Permission.Code)
            .ToList();

        if (requiredPermissions.Count > 0 && !requiredPermissions.All(userPermissions.Contains))
            return false;

        return true;
    }

    private static MenuItemResponse BuildMenuItem(Menu menu, List<Menu> allVisibleMenus)
    {
        var children = allVisibleMenus
            .Where(m => m.ParentMenuId == menu.Id)
            .OrderBy(m => m.DisplayOrder)
            .Select(m => BuildMenuItem(m, allVisibleMenus))
            .ToList();

        return new MenuItemResponse(
            Id: menu.Id,
            Code: menu.Code,
            Name: menu.Name,
            Route: menu.Route,
            Icon: menu.Icon,
            DisplayOrder: menu.DisplayOrder,
            IsEnabled: menu.IsEnabled,
            Children: children);
    }
}
