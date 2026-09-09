using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-A2 — tenant ownership for the RoleService WRITE paths (findings R-3 and R-5).
///
/// Mutability is deliberately narrower than visibility. A role may be written only when it
/// is owned by the current tenant AND is not a system role:
///
///   missing                                  → Role.NotFound
///   owned by another tenant                  → Role.NotFound   (indistinguishable, on purpose)
///   IsSystemRole == true                     → Role.SystemRole (protection beats ownership)
///   TenantId == null, IsSystemRole == false  → Role.UnownedRole (legacy shape, owned by nobody)
///   TenantId == current, not system          → writable
///
/// Every rejection is asserted twice: once on the returned error, and once on the persisted
/// row. An authorization test that only checks the status code cannot tell a refusal apart
/// from a refusal that mutated something on the way out — which is exactly the failure mode
/// R-5 had, where the old permission set was cleared before validation ran.
/// </summary>
public class RoleService_TenantOwnedWriteTests : IdentityServiceTestBase
{
    private readonly RoleService _sut;

    public RoleService_TenantOwnedWriteTests()
    {
        _sut = new RoleService(Roles, Permissions, UnitOfWork, TenantProvider);
    }

    private async Task<AppRole> PersistRoleAsync(string name, Guid? tenantId, bool isSystemRole = false)
    {
        var role = CreateTestRole(name, isSystemRole);
        role.TenantId = tenantId;
        return await PersistAsync(role);
    }

    private async Task<IReadOnlyList<Guid>> PermissionIdsForAsync(Guid roleId)
        => await Context.Set<AppRolePermission>()
            .Where(rp => rp.RoleId == roleId)
            .Select(rp => rp.PermissionId)
            .ToListAsync();

    // ────────────────────────── R-3 · UpdateAsync ──────────────────────────

    [Fact]
    public async Task Update_OwnedCustomRole_Succeeds()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistRoleAsync("Chennai Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("Chennai Shift Lead", "Updated"));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == role.Id);
        Assert.Equal("Chennai Shift Lead", stored.Name);
        Assert.Equal("Updated", stored.Description);
    }

    [Fact]
    public async Task Update_ForeignTenantRole_ReturnsNotFound()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var foreignRole = await PersistRoleAsync("Mumbai Floor Manager", tenantB.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(foreignRole.Id, new UpdateRoleRequest("Hijacked", "x"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Update_ForeignTenantRole_LeavesItUnchanged()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var foreignRole = await PersistRoleAsync("Mumbai Floor Manager", tenantB.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.UpdateAsync(foreignRole.Id, new UpdateRoleRequest("Hijacked", "x"));

        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == foreignRole.Id);
        Assert.Equal("Mumbai Floor Manager", stored.Name);
        Assert.Equal(tenantB.Id, stored.TenantId);
    }

    [Fact]
    public async Task Update_UnknownRole_ReturnsTheSameErrorAsAForeignRole()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var foreignRole = await PersistRoleAsync("Mumbai Floor Manager", tenantB.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var foreign = await _sut.UpdateAsync(foreignRole.Id, new UpdateRoleRequest("X", null));
        var unknown = await _sut.UpdateAsync(Guid.NewGuid(), new UpdateRoleRequest("X", null));

        Assert.True(foreign.IsFailure);
        Assert.True(unknown.IsFailure);
        Assert.Equal(unknown.Error.Code, foreign.Error.Code);
    }

    [Fact]
    public async Task Update_SystemRole_ReturnsSystemRoleError()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var systemRole = await PersistRoleAsync("Admin", tenantId: null, isSystemRole: true);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(systemRole.Id, new UpdateRoleRequest("Renamed", "x"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.SystemRole", result.Error.Code);
    }

    [Fact]
    public async Task Update_SystemRole_LeavesItUnchanged()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var systemRole = await PersistRoleAsync("Admin", tenantId: null, isSystemRole: true);
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.UpdateAsync(systemRole.Id, new UpdateRoleRequest("Renamed", "x"));

        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == systemRole.Id);
        Assert.Equal("Admin", stored.Name);
    }

    [Fact]
    public async Task Update_LegacyUnownedRole_ReturnsUnownedRoleError()
    {
        // The R-6 shape: created through POST /roles before role creation set an owner, so it
        // is visible to every tenant and owned by none. Refused rather than adopted — giving
        // it an owner here would be inventing ownership the owner has not approved.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var legacy = await PersistRoleAsync("Legacy Custom", tenantId: null, isSystemRole: false);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(legacy.Id, new UpdateRoleRequest("Adopted", "x"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.UnownedRole", result.Error.Code);
    }

    [Fact]
    public async Task Update_LegacyUnownedRole_LeavesItUnchangedAndStillUnowned()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var legacy = await PersistRoleAsync("Legacy Custom", tenantId: null, isSystemRole: false);
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.UpdateAsync(legacy.Id, new UpdateRoleRequest("Adopted", "x"));

        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == legacy.Id);
        Assert.Equal("Legacy Custom", stored.Name);
        Assert.Null(stored.TenantId);
    }

    [Fact]
    public async Task Update_TenantOwnedSystemRole_IsStillProtected()
    {
        // IsSystemRole governs protection independently of ownership, so owning a system role
        // does not make it writable. Guards the ordering inside the refusal check.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var ownedSystemRole = await PersistRoleAsync("Tenant Admin", tenantA.Id, isSystemRole: true);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(ownedSystemRole.Id, new UpdateRoleRequest("Renamed", "x"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.SystemRole", result.Error.Code);

        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == ownedSystemRole.Id);
        Assert.Equal("Tenant Admin", stored.Name);
    }

    [Fact]
    public async Task Update_ValidRole_DoesNotChangeTenantIdOrIsSystemRole()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistRoleAsync("Chennai Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("Renamed", "d"));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == role.Id);
        Assert.Equal(tenantA.Id, stored.TenantId);
        Assert.False(stored.IsSystemRole);
    }

    [Fact]
    public async Task Update_ValidRole_StillReturnsThePermissionProjection()
    {
        // The permission graph is loaded after authorization now; the response contract must
        // not have quietly lost it.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var permission = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        var role = CreateTestRole("Chennai Floor Manager");
        role.TenantId = tenantA.Id;
        role.RolePermissions.Add(new AppRolePermission { PermissionId = permission.Id });
        await PersistAsync(role);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("Renamed", "d"));

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Permissions);
        Assert.Equal("JOBCARD_CREATE", result.Value.Permissions[0].Code);
    }

    // ─────────────────── R-5 · AssignPermissionsAsync ───────────────────

    [Fact]
    public async Task AssignPermissions_OwnedCustomRole_Succeeds()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistRoleAsync("Chennai Floor Manager", tenantA.Id);
        var p1 = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        var p2 = await PersistAsync(CreateTestPermission("JOBCARD_APPROVE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.AssignPermissionsAsync(
            role.Id, new AssignPermissionsRequest(new[] { p1.Id, p2.Id }));

        Assert.True(result.IsSuccess);
        var assigned = await PermissionIdsForAsync(role.Id);
        Assert.Equal(2, assigned.Count);
        Assert.Contains(p1.Id, assigned);
        Assert.Contains(p2.Id, assigned);
    }

    [Fact]
    public async Task AssignPermissions_ForeignTenantRole_ReturnsNotFound()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var foreignRole = await PersistRoleAsync("Mumbai Floor Manager", tenantB.Id);
        var p1 = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.AssignPermissionsAsync(
            foreignRole.Id, new AssignPermissionsRequest(new[] { p1.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignPermissions_ForeignTenantRole_LeavesItsPermissionSetExactlyUnchanged()
    {
        // The R-5 CRITICAL case. The old implementation cleared the victim's set before any
        // check ran, so this asserts the persisted set, not merely the returned error.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));

        var original = await PersistAsync(CreateTestPermission("TENANT_B_ORIGINAL"));
        var foreignRole = CreateTestRole("Mumbai Floor Manager");
        foreignRole.TenantId = tenantB.Id;
        foreignRole.RolePermissions.Add(new AppRolePermission { PermissionId = original.Id });
        await PersistAsync(foreignRole);

        var attacker = await PersistAsync(CreateTestPermission("ATTACKER_CHOICE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.AssignPermissionsAsync(
            foreignRole.Id, new AssignPermissionsRequest(new[] { attacker.Id }));

        var assigned = await PermissionIdsForAsync(foreignRole.Id);
        Assert.Single(assigned);
        Assert.Equal(original.Id, assigned[0]);
        Assert.DoesNotContain(attacker.Id, assigned);
    }

    [Fact]
    public async Task AssignPermissions_UnknownRole_ReturnsTheSameErrorAsAForeignRole()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var foreignRole = await PersistRoleAsync("Mumbai Floor Manager", tenantB.Id);
        var p1 = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var foreign = await _sut.AssignPermissionsAsync(
            foreignRole.Id, new AssignPermissionsRequest(new[] { p1.Id }));
        var unknown = await _sut.AssignPermissionsAsync(
            Guid.NewGuid(), new AssignPermissionsRequest(new[] { p1.Id }));

        Assert.True(foreign.IsFailure);
        Assert.True(unknown.IsFailure);
        Assert.Equal(unknown.Error.Code, foreign.Error.Code);
    }

    [Fact]
    public async Task AssignPermissions_SystemRole_IsRejected()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var systemRole = await PersistRoleAsync("Admin", tenantId: null, isSystemRole: true);
        var p1 = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.AssignPermissionsAsync(
            systemRole.Id, new AssignPermissionsRequest(new[] { p1.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.SystemRole", result.Error.Code);
    }

    [Fact]
    public async Task AssignPermissions_SystemRole_LeavesItsPermissionSetUnchanged()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var original = await PersistAsync(CreateTestPermission("SYSTEM_ORIGINAL"));
        var systemRole = CreateTestRole("Admin", isSystemRole: true);
        systemRole.RolePermissions.Add(new AppRolePermission { PermissionId = original.Id });
        await PersistAsync(systemRole);

        var attacker = await PersistAsync(CreateTestPermission("ATTACKER_CHOICE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.AssignPermissionsAsync(
            systemRole.Id, new AssignPermissionsRequest(new[] { attacker.Id }));

        var assigned = await PermissionIdsForAsync(systemRole.Id);
        Assert.Single(assigned);
        Assert.Equal(original.Id, assigned[0]);
    }

    [Fact]
    public async Task AssignPermissions_LegacyUnownedRole_IsRejected()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var legacy = await PersistRoleAsync("Legacy Custom", tenantId: null, isSystemRole: false);
        var p1 = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.AssignPermissionsAsync(
            legacy.Id, new AssignPermissionsRequest(new[] { p1.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.UnownedRole", result.Error.Code);
    }

    [Fact]
    public async Task AssignPermissions_LegacyUnownedRole_LeavesItsPermissionSetUnchanged()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var original = await PersistAsync(CreateTestPermission("LEGACY_ORIGINAL"));
        var legacy = CreateTestRole("Legacy Custom");
        legacy.RolePermissions.Add(new AppRolePermission { PermissionId = original.Id });
        await PersistAsync(legacy);

        var attacker = await PersistAsync(CreateTestPermission("ATTACKER_CHOICE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.AssignPermissionsAsync(
            legacy.Id, new AssignPermissionsRequest(new[] { attacker.Id }));

        var assigned = await PermissionIdsForAsync(legacy.Id);
        Assert.Single(assigned);
        Assert.Equal(original.Id, assigned[0]);
    }

    [Fact]
    public async Task AssignPermissions_TenantOwnedSystemRole_IsStillProtected()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var ownedSystemRole = await PersistRoleAsync("Tenant Admin", tenantA.Id, isSystemRole: true);
        var p1 = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.AssignPermissionsAsync(
            ownedSystemRole.Id, new AssignPermissionsRequest(new[] { p1.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.SystemRole", result.Error.Code);
    }

    [Fact]
    public async Task AssignPermissions_UnknownPermissionId_IsRejected()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistRoleAsync("Chennai Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.AssignPermissionsAsync(
            role.Id, new AssignPermissionsRequest(new[] { Guid.NewGuid() }));

        Assert.True(result.IsFailure);
        Assert.Equal("Permission.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignPermissions_UnknownPermissionId_LeavesTheExistingSetIntact()
    {
        // Validation now runs before anything is removed. Under the old clear-then-validate
        // order the existing set was already staged for deletion by this point.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var original = await PersistAsync(CreateTestPermission("ORIGINAL"));
        var role = CreateTestRole("Chennai Floor Manager");
        role.TenantId = tenantA.Id;
        role.RolePermissions.Add(new AppRolePermission { PermissionId = original.Id });
        await PersistAsync(role);
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.AssignPermissionsAsync(
            role.Id, new AssignPermissionsRequest(new[] { Guid.NewGuid() }));

        var assigned = await PermissionIdsForAsync(role.Id);
        Assert.Single(assigned);
        Assert.Equal(original.Id, assigned[0]);
    }

    [Fact]
    public async Task AssignPermissions_WithOneValidAndOneUnknownId_AppliesNeither()
    {
        // No partial replacement: a request that is rejected must change nothing at all,
        // including the valid half of it.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var original = await PersistAsync(CreateTestPermission("ORIGINAL"));
        var valid = await PersistAsync(CreateTestPermission("VALID_NEW"));
        var role = CreateTestRole("Chennai Floor Manager");
        role.TenantId = tenantA.Id;
        role.RolePermissions.Add(new AppRolePermission { PermissionId = original.Id });
        await PersistAsync(role);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.AssignPermissionsAsync(
            role.Id, new AssignPermissionsRequest(new[] { valid.Id, Guid.NewGuid() }));

        Assert.True(result.IsFailure);
        var assigned = await PermissionIdsForAsync(role.Id);
        Assert.Single(assigned);
        Assert.Equal(original.Id, assigned[0]);
        Assert.DoesNotContain(valid.Id, assigned);
    }

    [Fact]
    public async Task AssignPermissions_ValidReplacement_RemovesOldAndAddsRequested()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var oldPerm = await PersistAsync(CreateTestPermission("OLD_PERM"));
        var newPerm = await PersistAsync(CreateTestPermission("NEW_PERM"));
        var role = CreateTestRole("Chennai Floor Manager");
        role.TenantId = tenantA.Id;
        role.RolePermissions.Add(new AppRolePermission { PermissionId = oldPerm.Id });
        await PersistAsync(role);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.AssignPermissionsAsync(
            role.Id, new AssignPermissionsRequest(new[] { newPerm.Id }));

        Assert.True(result.IsSuccess);
        var assigned = await PermissionIdsForAsync(role.Id);
        Assert.Single(assigned);
        Assert.Equal(newPerm.Id, assigned[0]);
        Assert.DoesNotContain(oldPerm.Id, assigned);
    }

    [Fact]
    public async Task AssignPermissions_ForeignRole_DoesNotTouchTheCallersOwnRoleEither()
    {
        // Guards against a refusal that resolves the wrong role and mutates something else.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));

        var ownPerm = await PersistAsync(CreateTestPermission("OWN_PERM"));
        var ownRole = CreateTestRole("Chennai Floor Manager");
        ownRole.TenantId = tenantA.Id;
        ownRole.RolePermissions.Add(new AppRolePermission { PermissionId = ownPerm.Id });
        await PersistAsync(ownRole);

        var foreignRole = await PersistRoleAsync("Mumbai Floor Manager", tenantB.Id);
        var other = await PersistAsync(CreateTestPermission("OTHER"));
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.AssignPermissionsAsync(
            foreignRole.Id, new AssignPermissionsRequest(new[] { other.Id }));

        var ownAssigned = await PermissionIdsForAsync(ownRole.Id);
        Assert.Single(ownAssigned);
        Assert.Equal(ownPerm.Id, ownAssigned[0]);
    }
}
