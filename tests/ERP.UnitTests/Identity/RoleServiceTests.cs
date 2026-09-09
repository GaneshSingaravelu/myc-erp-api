using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for RoleService. Verifies the repository-based refactor preserves
/// system-role guards, name uniqueness, and permission-assignment replacement.
/// </summary>
public class RoleServiceTests : IdentityServiceTestBase
{
    private readonly RoleService _sut;

    public RoleServiceTests()
    {
        _sut = new RoleService(Roles, Permissions, UnitOfWork, TenantProvider);
    }

    // ── GetByIdAsync ──

    [Fact]
    public async Task GetById_WhenRoleNotFound_ReturnsNotFound()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task GetById_ReturnsRoleWithPermissionsProjection()
    {
        var permission = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        var role = CreateTestRole("Admin");
        role.RolePermissions.Add(new AppRolePermission { PermissionId = permission.Id });
        await PersistAsync(role);

        var result = await _sut.GetByIdAsync(role.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Admin", result.Value.Name);
        Assert.Single(result.Value.Permissions);
        Assert.Equal("JOBCARD_CREATE", result.Value.Permissions[0].Code);
    }

    // ── GetAllAsync ──

    [Fact]
    public async Task GetAll_ReturnsRolesOrderedByName()
    {
        await PersistAsync(CreateTestRole("Operator"));
        await PersistAsync(CreateTestRole("Admin"));
        await PersistAsync(CreateTestRole("Viewer"));

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "Admin", "Operator", "Viewer" }, result.Value.Select(r => r.Name));
    }

    // ── CreateAsync ──

    [Fact]
    public async Task Create_WhenNameExists_ReturnsConflict()
    {
        await PersistAsync(CreateTestRole("Admin"));

        var result = await _sut.CreateAsync(new CreateRoleRequest("Admin", "duplicate"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NameExists", result.Error.Code);
    }

    [Fact]
    public async Task Create_PersistsAsNonSystemRole()
    {
        var result = await _sut.CreateAsync(new CreateRoleRequest("QA Lead", "Quality"));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Name == "QA Lead");
        Assert.False(stored.IsSystemRole);
        Assert.Equal("Quality", stored.Description);
    }

    // ── UpdateAsync ──

    [Fact]
    public async Task Update_WhenRoleNotFound_ReturnsNotFound()
    {
        var result = await _sut.UpdateAsync(Guid.NewGuid(), new UpdateRoleRequest("X", null));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Update_WhenSystemRole_ReturnsValidationFailure()
    {
        var sysRole = await PersistAsync(CreateTestRole("Admin", isSystemRole: true));

        var result = await _sut.UpdateAsync(sysRole.Id, new UpdateRoleRequest("Renamed", "x"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.SystemRole", result.Error.Code);
    }

    [Fact]
    public async Task Update_WhenCustomRole_PersistsChanges()
    {
        // Now needs an owning tenant: since S-A2 a role belongs to the current tenant or it
        // is not writable, so the historical TenantId-null fixture would be refused as
        // Role.UnownedRole rather than updated.
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        var role = CreateTestRole("QA Lead", isSystemRole: false);
        role.TenantId = tenant.Id;
        await PersistAsync(role);
        SetCurrentTenant(tenant.Id, "ABC");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("QA Manager", "Manages QA"));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == role.Id);
        Assert.Equal("QA Manager", stored.Name);
        Assert.Equal("Manages QA", stored.Description);
    }

    // ── DeleteAsync ──

    [Fact]
    public async Task Delete_WhenSystemRole_ReturnsValidationFailure()
    {
        var sysRole = await PersistAsync(CreateTestRole("Admin", isSystemRole: true));

        var result = await _sut.DeleteAsync(sysRole.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.SystemRole", result.Error.Code);
    }

    [Fact]
    public async Task Delete_WhenCustomRole_SoftDeletesViaInterceptor()
    {
        var role = await PersistAsync(CreateTestRole("QA Lead"));

        var result = await _sut.DeleteAsync(role.Id);

        Assert.True(result.IsSuccess);
        var raw = await Context.Set<AppRole>().IgnoreQueryFilters().FirstAsync(r => r.Id == role.Id);
        Assert.True(raw.IsDeleted);
    }

    // ── AssignPermissionsAsync ──

    [Fact]
    public async Task AssignPermissions_WhenRoleNotFound_ReturnsNotFound()
    {
        var permission = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        var request = new AssignPermissionsRequest(new[] { permission.Id });

        var result = await _sut.AssignPermissionsAsync(Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignPermissions_WhenPermissionMissing_ReturnsNotFound()
    {
        // Ownership is established first now, so this fixture must be a role the current
        // tenant owns — otherwise the request is refused before permission validation runs.
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        var role = CreateTestRole("Admin");
        role.TenantId = tenant.Id;
        await PersistAsync(role);
        SetCurrentTenant(tenant.Id, "ABC");

        var request = new AssignPermissionsRequest(new[] { Guid.NewGuid() });

        var result = await _sut.AssignPermissionsAsync(role.Id, request);

        Assert.True(result.IsFailure);
        Assert.Equal("Permission.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignPermissions_ReplacesExistingAssignments()
    {
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        var role = CreateTestRole("Admin");
        role.TenantId = tenant.Id;
        await PersistAsync(role);
        SetCurrentTenant(tenant.Id, "ABC");

        var oldPerm = await PersistAsync(CreateTestPermission("OLD_PERM"));
        var newPerm1 = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        var newPerm2 = await PersistAsync(CreateTestPermission("JOBCARD_APPROVE"));

        await PersistAsync(new AppRolePermission { RoleId = role.Id, PermissionId = oldPerm.Id });

        var request = new AssignPermissionsRequest(new[] { newPerm1.Id, newPerm2.Id });
        var result = await _sut.AssignPermissionsAsync(role.Id, request);

        Assert.True(result.IsSuccess);
        var assigned = await Context.Set<AppRolePermission>()
            .Where(rp => rp.RoleId == role.Id)
            .Select(rp => rp.PermissionId)
            .ToListAsync();

        Assert.Equal(2, assigned.Count);
        Assert.Contains(newPerm1.Id, assigned);
        Assert.Contains(newPerm2.Id, assigned);
        Assert.DoesNotContain(oldPerm.Id, assigned);
    }

    // ── GetAllPermissionsAsync ──

    [Fact]
    public async Task GetAllPermissions_OrdersByCategoryThenCode()
    {
        await PersistAsync(CreateTestPermission("JOBCARD_CREATE", "Production"));
        await PersistAsync(CreateTestPermission("USER_CREATE", "Admin"));
        await PersistAsync(CreateTestPermission("JOBCARD_APPROVE", "Production"));
        await PersistAsync(CreateTestPermission("ROLE_CREATE", "Admin"));

        var result = await _sut.GetAllPermissionsAsync();

        Assert.True(result.IsSuccess);
        var codes = result.Value.Select(p => p.Code).ToList();
        // Admin/ROLE_CREATE before Admin/USER_CREATE; both before any Production entry.
        Assert.Equal(new[] { "ROLE_CREATE", "USER_CREATE", "JOBCARD_APPROVE", "JOBCARD_CREATE" }, codes);
    }
}
