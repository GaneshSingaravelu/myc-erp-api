using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-A3 — role creation ownership and name normalization (findings R-6 and R-7).
///
/// R-6: a role created through POST /roles belongs to the calling tenant and is not a
/// system role. Neither value is reachable from the request.
///
/// R-7: Name and NormalizedName can no longer diverge. Creation used to leave
/// NormalizedName at its empty-string default, so every created role collided on the
/// (NormalizedName, TenantId) unique index after the first; update used to change Name
/// alone, so a renamed role kept answering to its old normalized name.
///
/// Uniqueness is deliberately stricter than the database index: a name is taken if it
/// exists in this tenant OR globally, because two same-named roles both visible in one
/// tenant are ambiguous to anyone reading a role list.
/// </summary>
public class RoleService_CreationAndNamingTests : IdentityServiceTestBase
{
    private readonly RoleService _sut;

    public RoleService_CreationAndNamingTests()
    {
        _sut = new RoleService(Roles, Permissions, UnitOfWork, TenantProvider);
    }

    private async Task<AppRole> PersistRoleAsync(string name, Guid? tenantId, bool isSystemRole = false)
    {
        var role = CreateTestRole(name, isSystemRole);
        role.TenantId = tenantId;
        return await PersistAsync(role);
    }

    private Task<List<AppRole>> RolesNamedAsync(string normalizedName)
        => Context.Set<AppRole>().Where(r => r.NormalizedName == normalizedName).ToListAsync();

    // ───────────────────────── R-6 · creation ownership ─────────────────────────

    [Fact]
    public async Task Create_BindsRoleToCurrentTenantAndNormalizesName()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", "Runs the floor"));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == result.Value.Id);
        Assert.Equal(tenantA.Id, stored.TenantId);
        Assert.False(stored.IsSystemRole);
        Assert.Equal("FLOOR MANAGER", stored.NormalizedName);
        Assert.Equal("Floor Manager", stored.Name);
    }

    [Fact]
    public async Task Create_UnderTenantB_BindsToTenantB()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        SetCurrentTenant(tenantB.Id, "BBB");

        var result = await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", null));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == result.Value.Id);
        Assert.Equal(tenantB.Id, stored.TenantId);
        Assert.NotEqual(tenantA.Id, stored.TenantId);
    }

    [Fact]
    public async Task Create_TwoDifferentRolesInTheSameTenant_BothPersist()
    {
        // The R-7 regression test. Before the fix both roles were written with
        // NormalizedName = "", so the second one violated the (NormalizedName, TenantId)
        // unique index and the endpoint was effectively single-use per tenant.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var first = await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", null));
        var second = await _sut.CreateAsync(new CreateRoleRequest("Shift Lead", null));

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);

        var stored = await Context.Set<AppRole>()
            .Where(r => r.TenantId == tenantA.Id)
            .OrderBy(r => r.Name)
            .ToListAsync();

        Assert.Equal(2, stored.Count);
        Assert.Equal(new[] { "FLOOR MANAGER", "SHIFT LEAD" }, stored.Select(r => r.NormalizedName));
        Assert.DoesNotContain(stored, r => string.IsNullOrEmpty(r.NormalizedName));
    }

    [Fact]
    public async Task Create_NeverProducesASystemRoleOrAGlobalRole()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", null));

        var stored = await Context.Set<AppRole>().FirstAsync(r => r.NormalizedName == "FLOOR MANAGER");
        Assert.False(stored.IsSystemRole);
        Assert.NotNull(stored.TenantId);
        Assert.Equal(tenantA.Id, stored.TenantId);
    }

    [Fact]
    public async Task Create_ReturnsTheUsualDtoShape()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", "Runs the floor"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Floor Manager", result.Value.Name);
        Assert.Equal("Runs the floor", result.Value.Description);
        Assert.False(result.Value.IsSystemRole);
        Assert.Empty(result.Value.Permissions);
    }

    // ───────────────────────── R-7 · creation uniqueness ─────────────────────────

    [Fact]
    public async Task Create_DuplicateNameInSameTenant_IsRejectedAndCreatesNothing()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        await PersistRoleAsync("Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", "second"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NameExists", result.Error.Code);
        Assert.Single(await RolesNamedAsync("FLOOR MANAGER"));
    }

    [Fact]
    public async Task Create_CaseOnlyDuplicateInSameTenant_IsRejected()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        await PersistRoleAsync("Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.CreateAsync(new CreateRoleRequest("floor manager", "second"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NameExists", result.Error.Code);
        Assert.Single(await RolesNamedAsync("FLOOR MANAGER"));
    }

    [Fact]
    public async Task Create_NameCollidingWithAGlobalSystemRole_IsRejected()
    {
        // The database index would permit this — (ADMIN, null) and (ADMIN, tenantA) are
        // different entries — but two roles called Admin visible in one tenant is ambiguous.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        await PersistRoleAsync("Admin", tenantId: null, isSystemRole: true);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.CreateAsync(new CreateRoleRequest("Admin", "shadow"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NameExists", result.Error.Code);
        Assert.DoesNotContain(await RolesNamedAsync("ADMIN"), r => r.TenantId == tenantA.Id);
    }

    [Fact]
    public async Task Create_SameCustomNameInTwoTenants_BothSucceed()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));

        SetCurrentTenant(tenantA.Id, "AAA");
        var inA = await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", null));

        SetCurrentTenant(tenantB.Id, "BBB");
        var inB = await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", null));

        Assert.True(inA.IsSuccess);
        Assert.True(inB.IsSuccess);

        var stored = await RolesNamedAsync("FLOOR MANAGER");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, r => r.TenantId == tenantA.Id);
        Assert.Contains(stored, r => r.TenantId == tenantB.Id);
    }

    [Fact]
    public async Task Create_NameOwnedOnlyByAnotherTenant_IsAllowed()
    {
        // The uniqueness query must not reach across tenants, only into global scope.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        await PersistRoleAsync("Floor Manager", tenantB.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.CreateAsync(new CreateRoleRequest("Floor Manager", null));

        Assert.True(result.IsSuccess);
    }

    // ───────────────────────── R-7 · update normalization ─────────────────────────

    [Fact]
    public async Task Update_Rename_WritesBothNameAndNormalizedName()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistRoleAsync("Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("Shift Lead", "renamed"));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == role.Id);
        Assert.Equal("Shift Lead", stored.Name);
        Assert.Equal("SHIFT LEAD", stored.NormalizedName);
    }

    [Fact]
    public async Task Update_RenameOntoAnotherRoleInTheSameTenant_IsRejected()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var target = await PersistRoleAsync("Floor Manager", tenantA.Id);
        await PersistRoleAsync("Shift Lead", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(target.Id, new UpdateRoleRequest("Shift Lead", "clash"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NameExists", result.Error.Code);
    }

    [Fact]
    public async Task Update_RenameOntoACaseOnlyVariantOfAnotherRole_IsRejected()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var target = await PersistRoleAsync("Floor Manager", tenantA.Id);
        await PersistRoleAsync("Shift Lead", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(target.Id, new UpdateRoleRequest("shift lead", "clash"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NameExists", result.Error.Code);
    }

    [Fact]
    public async Task Update_RenameOntoAGlobalSystemRoleName_IsRejected()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        await PersistRoleAsync("Admin", tenantId: null, isSystemRole: true);
        var role = await PersistRoleAsync("Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("Admin", "shadow"));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NameExists", result.Error.Code);
    }

    [Fact]
    public async Task Update_CaseOnlySelfRename_Succeeds()
    {
        // Same role, same normalized value. It must not be reported as conflicting with
        // itself — which is exactly what the excludeRoleId argument is for.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistRoleAsync("Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("floor manager", "cased"));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == role.Id);
        Assert.Equal("floor manager", stored.Name);
        Assert.Equal("FLOOR MANAGER", stored.NormalizedName);
    }

    [Fact]
    public async Task Update_WithAnUnchangedName_Succeeds()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistRoleAsync("Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("Floor Manager", "new description"));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == role.Id);
        Assert.Equal("new description", stored.Description);
        Assert.Equal("FLOOR MANAGER", stored.NormalizedName);
    }

    [Fact]
    public async Task Update_RenameToANameUsedOnlyByAnotherTenant_Succeeds()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        await PersistRoleAsync("Shift Lead", tenantB.Id);
        var role = await PersistRoleAsync("Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("Shift Lead", null));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == role.Id);
        Assert.Equal("SHIFT LEAD", stored.NormalizedName);
        Assert.Equal(tenantA.Id, stored.TenantId);
    }

    [Fact]
    public async Task Update_ValidRename_LeavesOwnershipAndProtectionAlone()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistRoleAsync("Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.UpdateAsync(role.Id, new UpdateRoleRequest("Shift Lead", null));

        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == role.Id);
        Assert.Equal(tenantA.Id, stored.TenantId);
        Assert.False(stored.IsSystemRole);
    }

    [Fact]
    public async Task Update_RejectedForNameConflict_LeavesEveryFieldUnchanged()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var target = await PersistRoleAsync("Floor Manager", tenantA.Id);
        await PersistRoleAsync("Shift Lead", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        await _sut.UpdateAsync(target.Id, new UpdateRoleRequest("Shift Lead", "clash"));

        var stored = await Context.Set<AppRole>().FirstAsync(r => r.Id == target.Id);
        Assert.Equal("Floor Manager", stored.Name);
        Assert.Equal("FLOOR MANAGER", stored.NormalizedName);
        Assert.Equal(tenantA.Id, stored.TenantId);
        Assert.False(stored.IsSystemRole);
    }

    [Fact]
    public async Task Update_ForeignRole_StillAnswersNotFoundRatherThanANameConflict()
    {
        // Authorization runs first, so a caller cannot use a rename to probe whether some
        // name is taken inside a tenant they cannot see.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var foreignRole = await PersistRoleAsync("Mumbai Floor Manager", tenantB.Id);
        await PersistRoleAsync("Shift Lead", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.UpdateAsync(foreignRole.Id, new UpdateRoleRequest("Shift Lead", null));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Update_SystemAndUnownedRefusalsStillPrecedeNameChecks()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var systemRole = await PersistRoleAsync("Admin", tenantId: null, isSystemRole: true);
        var legacy = await PersistRoleAsync("Legacy Custom", tenantId: null, isSystemRole: false);
        await PersistRoleAsync("Shift Lead", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var system = await _sut.UpdateAsync(systemRole.Id, new UpdateRoleRequest("Shift Lead", null));
        var unowned = await _sut.UpdateAsync(legacy.Id, new UpdateRoleRequest("Shift Lead", null));

        Assert.Equal("Role.SystemRole", system.Error.Code);
        Assert.Equal("Role.UnownedRole", unowned.Error.Code);
    }
}
