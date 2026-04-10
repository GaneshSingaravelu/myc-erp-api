using ERP.Identity.DTOs;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace ERP.Identity.Services;

/// <summary>
/// MENU SERVICE — Builds the permission-filtered sidebar menu tree.
///
/// FLOW:
/// ======
/// 1. Load user's permissions (via roles for the given tenant)
/// 2. Load all modules + menus + menu permissions
/// 3. Resolve feature flags for the tenant
/// 4. Filter: keep menus where user has required permissions AND feature flag is enabled
/// 5. Build hierarchical tree (modules → top-level menus → children)
///
/// Angular calls this once after login, caches the result in the store.
/// </summary>
public class MenuService : IMenuService
{
    private readonly MasterDbContext _context;

    public MenuService(MasterDbContext context)
    {
        _context = context;
    }

    public async Task<Result<IReadOnlyList<ModuleMenuResponse>>> GetMyMenusAsync(Guid userId, Guid? tenantId)
    {
        // Step 1: Get user's permission codes
        var userPermissions = await _context.Set<AppUserRole>()
            .Where(ur => ur.UserId == userId && ur.IsActive)
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct()
            .ToListAsync();

        var permissionSet = userPermissions.ToHashSet();

        // Step 2: Get tenant feature flag overrides
        var tenantFlags = tenantId.HasValue
            ? await _context.Set<TenantFeatureFlag>()
                .Where(tf => tf.TenantId == tenantId.Value)
                .ToDictionaryAsync(tf => tf.FeatureFlag.Code, tf => tf.IsEnabled)
            : new Dictionary<string, bool>();

        // Get global feature flags (fallback)
        var globalFlags = await _context.Set<FeatureFlag>()
            .ToDictionaryAsync(f => f.Code, f => f.IsEnabled);

        // Step 3: Load all active modules with their menus and menu permissions
        var modules = await _context.Set<Module>()
            .Where(m => m.IsActive)
            .Include(m => m.Menus.Where(menu => menu.IsVisible))
                .ThenInclude(menu => menu.MenuPermissions)
                    .ThenInclude(mp => mp.Permission)
            .OrderBy(m => m.DisplayOrder)
            .ToListAsync();

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
        Dictionary<string, bool> tenantFlags,
        Dictionary<string, bool> globalFlags)
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
        Dictionary<string, bool> tenantFlags,
        Dictionary<string, bool> globalFlags)
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
