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
    private readonly IFeatureFlagRepository _featureFlags;

    public MenuService(
        IUserRepository users,
        IMenuRepository menus,
        IFeatureFlagRepository featureFlags)
    {
        _users = users;
        _menus = menus;
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
