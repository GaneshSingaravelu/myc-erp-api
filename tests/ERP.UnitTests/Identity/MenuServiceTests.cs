using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for MenuService. The service is the heart of the dynamic sidebar
/// — these tests pin its filtering rules so the P0 menu/navbar bug can't recur.
///
/// Filtering matrix:
///   • A menu is visible iff (a) all required MenuPermissions are held by the user
///     AND (b) the FeatureFlag (if any) resolves to true via tenant override or
///     global default.
///   • Modules with no visible menus are dropped from the response.
///   • The remaining menus are returned as a hierarchical tree (ParentMenuId).
/// </summary>
public class MenuServiceTests : IdentityServiceTestBase
{
    private readonly MenuService _sut;

    public MenuServiceTests()
    {
        _sut = new MenuService(Users, Menus, FeatureFlags);
    }

    // ── Test fixture builders ──

    /// <summary>
    /// Seed a user with a single role granting the listed permission codes.
    /// Returns the user id for use as the GetMyMenusAsync subject.
    /// </summary>
    private async Task<Guid> SeedUserWithPermissionsAsync(params string[] permissionCodes)
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        await PersistAsync(new AppUserRole { UserId = user.Id, RoleId = role.Id });

        foreach (var code in permissionCodes)
        {
            var perm = await PersistAsync(CreateTestPermission(code));
            await PersistAsync(new AppRolePermission { RoleId = role.Id, PermissionId = perm.Id });
        }

        return user.Id;
    }

    /// <summary>Persist a Module with the given menus pre-attached.</summary>
    private async Task<Module> SeedModuleAsync(string code, params Menu[] menus)
    {
        var module = new Module
        {
            Code = code,
            Name = $"{code} Module",
            DisplayOrder = 1,
            IsActive = true
        };
        foreach (var m in menus)
            module.Menus.Add(m);

        return await PersistAsync(module);
    }

    private static Menu MakeMenu(
        string code,
        int order = 1,
        bool isVisible = true,
        string? featureFlag = null,
        Guid? parentMenuId = null)
    {
        return new Menu
        {
            Code = code,
            Name = code,
            Route = $"/{code.ToLowerInvariant()}",
            DisplayOrder = order,
            IsVisible = isVisible,
            IsEnabled = true,
            FeatureFlag = featureFlag,
            ParentMenuId = parentMenuId
        };
    }

    // ── Tests ──

    [Fact]
    public async Task GetMyMenus_WhenUserHasNoPermissions_ReturnsEmpty()
    {
        var userId = await SeedUserWithPermissionsAsync();
        var permission = await PersistAsync(CreateTestPermission("WO_VIEW"));
        var menu = MakeMenu("WORK_ORDERS");
        menu.MenuPermissions.Add(new MenuPermission { PermissionId = permission.Id, IsRequired = true });
        await SeedModuleAsync("PRODUCTION", menu);

        var result = await _sut.GetMyMenusAsync(userId, tenantId: null);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task GetMyMenus_WhenMenuHasNoPermissions_AlwaysVisible()
    {
        var userId = await SeedUserWithPermissionsAsync();
        await SeedModuleAsync("PRODUCTION", MakeMenu("DASHBOARD"));

        var result = await _sut.GetMyMenusAsync(userId, tenantId: null);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Single(result.Value[0].Menus);
        Assert.Equal("DASHBOARD", result.Value[0].Menus[0].Code);
    }

    [Fact]
    public async Task GetMyMenus_WhenAnyRequiredPermissionMissing_HidesMenu()
    {
        var userId = await SeedUserWithPermissionsAsync("WO_VIEW");
        var viewPerm = await Context.Set<Permission>().FirstAsync(p => p.Code == "WO_VIEW");
        var approvePerm = await PersistAsync(CreateTestPermission("WO_APPROVE"));

        var menu = MakeMenu("WORK_ORDERS");
        menu.MenuPermissions.Add(new MenuPermission { PermissionId = viewPerm.Id, IsRequired = true });
        menu.MenuPermissions.Add(new MenuPermission { PermissionId = approvePerm.Id, IsRequired = true });
        await SeedModuleAsync("PRODUCTION", menu);

        var result = await _sut.GetMyMenusAsync(userId, tenantId: null);

        // User has WO_VIEW but lacks WO_APPROVE → AND logic hides menu → module dropped.
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task GetMyMenus_OptionalPermissionDoesNotBlockVisibility()
    {
        var userId = await SeedUserWithPermissionsAsync("WO_VIEW");
        var viewPerm = await Context.Set<Permission>().FirstAsync(p => p.Code == "WO_VIEW");
        var optionalPerm = await PersistAsync(CreateTestPermission("WO_APPROVE"));

        var menu = MakeMenu("WORK_ORDERS");
        menu.MenuPermissions.Add(new MenuPermission { PermissionId = viewPerm.Id, IsRequired = true });
        menu.MenuPermissions.Add(new MenuPermission { PermissionId = optionalPerm.Id, IsRequired = false });
        await SeedModuleAsync("PRODUCTION", menu);

        var result = await _sut.GetMyMenusAsync(userId, tenantId: null);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value[0].Menus);
    }

    [Fact]
    public async Task GetMyMenus_WhenFeatureFlagDisabledGlobally_HidesMenu()
    {
        var userId = await SeedUserWithPermissionsAsync();
        await PersistAsync(new FeatureFlag { Code = "PROD_V2", Name = "Production V2", IsEnabled = false });
        await SeedModuleAsync("PRODUCTION", MakeMenu("WO_BETA", featureFlag: "PROD_V2"));

        var result = await _sut.GetMyMenusAsync(userId, tenantId: null);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task GetMyMenus_TenantOverrideEnablesGloballyDisabledFlag()
    {
        var userId = await SeedUserWithPermissionsAsync();
        var tenant = await PersistAsync(CreateTestTenant("ACME"));
        var flag = await PersistAsync(new FeatureFlag { Code = "PROD_V2", Name = "Production V2", IsEnabled = false });
        await PersistAsync(new TenantFeatureFlag
        {
            TenantId = tenant.Id,
            FeatureFlagId = flag.Id,
            IsEnabled = true
        });
        await SeedModuleAsync("PRODUCTION", MakeMenu("WO_BETA", featureFlag: "PROD_V2"));

        var result = await _sut.GetMyMenusAsync(userId, tenant.Id);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("WO_BETA", result.Value[0].Menus[0].Code);
    }

    [Fact]
    public async Task GetMyMenus_TenantOverrideDisablesGloballyEnabledFlag()
    {
        var userId = await SeedUserWithPermissionsAsync();
        var tenant = await PersistAsync(CreateTestTenant("ACME"));
        var flag = await PersistAsync(new FeatureFlag { Code = "QA_DASHBOARD", Name = "QA", IsEnabled = true });
        await PersistAsync(new TenantFeatureFlag
        {
            TenantId = tenant.Id,
            FeatureFlagId = flag.Id,
            IsEnabled = false
        });
        await SeedModuleAsync("QUALITY", MakeMenu("QA_DASH", featureFlag: "QA_DASHBOARD"));

        var result = await _sut.GetMyMenusAsync(userId, tenant.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task GetMyMenus_BuildsParentChildHierarchyOrderedByDisplayOrder()
    {
        var userId = await SeedUserWithPermissionsAsync();

        // Build a 2-level menu: parent "WORK_ORDERS" → children "WO_LIST" (order 1), "WO_CREATE" (order 2)
        var parent = MakeMenu("WORK_ORDERS", order: 1);
        await SeedModuleAsync("PRODUCTION", parent);

        var child2 = MakeMenu("WO_CREATE", order: 2, parentMenuId: parent.Id);
        var child1 = MakeMenu("WO_LIST", order: 1, parentMenuId: parent.Id);
        // Re-attach children to the same module so they're loaded by the visible-menus include.
        var module = await Context.Set<Module>().FindAsync(parent.ModuleId);
        Assert.NotNull(module);
        module!.Menus.Add(child2);
        module.Menus.Add(child1);
        await Context.SaveChangesAsync();

        var result = await _sut.GetMyMenusAsync(userId, tenantId: null);

        Assert.True(result.IsSuccess);
        var topLevel = Assert.Single(result.Value[0].Menus);
        Assert.Equal("WORK_ORDERS", topLevel.Code);
        Assert.Equal(2, topLevel.Children.Count);
        Assert.Equal(new[] { "WO_LIST", "WO_CREATE" }, topLevel.Children.Select(c => c.Code));
    }

    [Fact]
    public async Task GetMyMenus_DropsModulesWithNoVisibleMenus()
    {
        var userId = await SeedUserWithPermissionsAsync("WO_VIEW");
        var viewPerm = await Context.Set<Permission>().FirstAsync(p => p.Code == "WO_VIEW");
        var qaPerm = await PersistAsync(CreateTestPermission("QA_VIEW"));

        var visibleMenu = MakeMenu("WORK_ORDERS");
        visibleMenu.MenuPermissions.Add(new MenuPermission { PermissionId = viewPerm.Id, IsRequired = true });
        await SeedModuleAsync("PRODUCTION", visibleMenu);

        var hiddenMenu = MakeMenu("QA_DASH");
        hiddenMenu.MenuPermissions.Add(new MenuPermission { PermissionId = qaPerm.Id, IsRequired = true });
        await SeedModuleAsync("QUALITY", hiddenMenu);

        var result = await _sut.GetMyMenusAsync(userId, tenantId: null);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("PRODUCTION", result.Value[0].Code);
    }
}
