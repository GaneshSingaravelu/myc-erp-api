using ERP.Identity.Entities;
using ERP.Identity.Services;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-A1 — tenant scoping of the RoleService READ paths (findings R-1 and R-2).
///
/// The rule under test, and the only rule these tests encode:
///
///   TenantId == null              → system role, visible in EVERY tenant
///   TenantId == CurrentTenantId   → custom role, visible ONLY in its owning tenant
///   TenantId == someone else's    → not visible, and not distinguishable from absent
///
/// Two things are deliberate here. First, every fixture persists a REAL Tenant row and
/// uses distinct tenant GUIDs, so a role is scoped to something that actually exists —
/// an InMemory provider will happily accept a dangling foreign key and let a test pass
/// while proving nothing. Second, the current tenant is always supplied through
/// <c>SetCurrentTenant</c>, i.e. through ITenantProvider, never by handing the service a
/// tenant id; a test that bypassed the provider would not be exercising the production
/// authorization path at all.
/// </summary>
public class RoleService_TenantScopedReadTests : IdentityServiceTestBase
{
    private readonly RoleService _sut;

    public RoleService_TenantScopedReadTests()
    {
        _sut = new RoleService(Roles, Permissions, UnitOfWork, TenantProvider);
    }

    /// <summary>A role owned by one specific tenant.</summary>
    private async Task<AppRole> PersistTenantRoleAsync(string name, Guid tenantId)
    {
        var role = CreateTestRole(name);
        role.TenantId = tenantId;
        return await PersistAsync(role);
    }

    /// <summary>A system role: TenantId stays null, so every tenant may see it.</summary>
    private Task<AppRole> PersistSystemRoleAsync(string name = "Admin")
        => PersistAsync(CreateTestRole(name, isSystemRole: true));

    // ── GetAllAsync ──

    [Fact]
    public async Task GetAll_TenantA_SeesSystemRole()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        await PersistSystemRoleAsync("Admin");
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value, r => r.Name == "Admin");
    }

    [Fact]
    public async Task GetAll_TenantB_SeesTheSameSystemRole()
    {
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        await PersistSystemRoleAsync("Admin");
        SetCurrentTenant(tenantB.Id, "BBB");

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value, r => r.Name == "Admin");
    }

    [Fact]
    public async Task GetAll_TenantA_SeesItsOwnCustomRole()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        await PersistTenantRoleAsync("Chennai Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value, r => r.Name == "Chennai Floor Manager");
    }

    [Fact]
    public async Task GetAll_TenantA_DoesNotSeeTenantBCustomRole()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        await PersistTenantRoleAsync("Mumbai Floor Manager", tenantB.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Value, r => r.Name == "Mumbai Floor Manager");
    }

    [Fact]
    public async Task GetAll_TenantB_SeesItsOwnCustomRole()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        await PersistTenantRoleAsync("Chennai Floor Manager", tenantA.Id);
        await PersistTenantRoleAsync("Mumbai Floor Manager", tenantB.Id);
        SetCurrentTenant(tenantB.Id, "BBB");

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains(result.Value, r => r.Name == "Mumbai Floor Manager");
        Assert.DoesNotContain(result.Value, r => r.Name == "Chennai Floor Manager");
    }

    [Fact]
    public async Task GetAll_DoesNotLeakForeignRolePermissions()
    {
        // The permission graph is the sensitive part of a role: it maps out what another
        // tenant's privilege model actually grants. If the role is filtered out, its
        // permissions must go with it.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));

        var secret = await PersistAsync(CreateTestPermission("TENANT_B_SECRET"));
        var foreignRole = CreateTestRole("Mumbai Floor Manager");
        foreignRole.TenantId = tenantB.Id;
        foreignRole.RolePermissions.Add(new AppRolePermission { PermissionId = secret.Id });
        await PersistAsync(foreignRole);

        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Value, r => r.Id == foreignRole.Id);
        Assert.DoesNotContain(
            result.Value.SelectMany(r => r.Permissions),
            p => p.Code == "TENANT_B_SECRET");
    }

    [Fact]
    public async Task GetAll_WithBothTenantsSeeded_ReturnsSystemRoleExactlyOnce()
    {
        // A system role is reachable through the "TenantId == null" arm for every tenant.
        // Guards against a future scoping rewrite that unions two queries and duplicates it.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        await PersistSystemRoleAsync("Admin");
        await PersistTenantRoleAsync("Chennai Floor Manager", tenantA.Id);
        await PersistTenantRoleAsync("Mumbai Floor Manager", tenantB.Id);

        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value, r => r.Name == "Admin");
        Assert.Equal(2, result.Value.Count);
    }

    [Fact]
    public async Task GetAll_PreservesNameOrderingAcrossSystemAndTenantRoles()
    {
        // Ordering is existing behaviour unrelated to tenant scope; scoping must not
        // reorder what survives the filter.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        await PersistSystemRoleAsync("Viewer");
        await PersistTenantRoleAsync("Auditor", tenantA.Id);
        await PersistSystemRoleAsync("Manager");
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "Auditor", "Manager", "Viewer" }, result.Value.Select(r => r.Name));
    }

    // ── GetByIdAsync ──

    [Fact]
    public async Task GetById_OwnTenantCustomRole_Succeeds()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistTenantRoleAsync("Chennai Floor Manager", tenantA.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetByIdAsync(role.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Chennai Floor Manager", result.Value.Name);
    }

    [Fact]
    public async Task GetById_ForeignTenantCustomRole_ReturnsNotFound()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var foreignRole = await PersistTenantRoleAsync("Mumbai Floor Manager", tenantB.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetByIdAsync(foreignRole.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsTheSameErrorAsAForeignRole()
    {
        // The point of the assertion: a caller holding a GUID must not be able to tell
        // "this role belongs to someone else" from "no such role". Same code, both ways.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var foreignRole = await PersistTenantRoleAsync("Mumbai Floor Manager", tenantB.Id);
        SetCurrentTenant(tenantA.Id, "AAA");

        var foreign = await _sut.GetByIdAsync(foreignRole.Id);
        var unknown = await _sut.GetByIdAsync(Guid.NewGuid());

        Assert.True(foreign.IsFailure);
        Assert.True(unknown.IsFailure);
        Assert.Equal(unknown.Error.Code, foreign.Error.Code);
    }

    [Fact]
    public async Task GetById_SystemRole_IsVisibleInTenantA()
    {
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var systemRole = await PersistSystemRoleAsync("Admin");
        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetByIdAsync(systemRole.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Admin", result.Value.Name);
    }

    [Fact]
    public async Task GetById_SystemRole_IsVisibleInTenantB()
    {
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var systemRole = await PersistSystemRoleAsync("Admin");
        SetCurrentTenant(tenantB.Id, "BBB");

        var result = await _sut.GetByIdAsync(systemRole.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("Admin", result.Value.Name);
    }

    [Fact]
    public async Task GetById_VisibleRole_StillCarriesItsPermissions()
    {
        // Scoping must filter roles, not quietly drop the Include chain.
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var permission = await PersistAsync(CreateTestPermission("JOBCARD_CREATE"));
        var role = CreateTestRole("Chennai Floor Manager");
        role.TenantId = tenantA.Id;
        role.RolePermissions.Add(new AppRolePermission { PermissionId = permission.Id });
        await PersistAsync(role);

        SetCurrentTenant(tenantA.Id, "AAA");

        var result = await _sut.GetByIdAsync(role.Id);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.Permissions);
        Assert.Equal("JOBCARD_CREATE", result.Value.Permissions[0].Code);
    }
}
