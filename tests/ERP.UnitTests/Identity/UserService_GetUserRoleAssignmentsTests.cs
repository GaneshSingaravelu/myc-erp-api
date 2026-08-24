using ERP.Identity.Entities;
using ERP.Identity.Services;

namespace ERP.UnitTests.Identity;

/// <summary>
/// `GET /users/{id}/roles` — tenant-scoped role reads.
///
/// This endpoint exists because `UserDto.Roles` is a flat list of role names
/// collapsed across every tenant. With assignment keyed on
/// (UserId, RoleId, TenantId), that flat list cannot say which roles belong to
/// which tenant, so a caller editing one tenant's roles could write roles into
/// the wrong one. The assertions below are mostly about keeping tenant identity
/// intact through the projection.
///
/// The InMemory base is sufficient here: nothing under test depends on foreign
/// keys or unique indexes — it is a projection plus a filter. FK-sensitive
/// behaviour lives in UserService_AssignRolesRelationalTests.
/// </summary>
public class UserService_GetUserRoleAssignmentsTests : IdentityServiceTestBase
{
    private readonly UserService _sut;

    public UserService_GetUserRoleAssignmentsTests()
    {
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork);
    }

    private async Task<AppUserRole> AssignAsync(
        Guid userId, Guid roleId, Guid tenantId, bool isActive = true) =>
        await PersistAsync(new AppUserRole
        {
            UserId = userId,
            RoleId = roleId,
            TenantId = tenantId,
            IsActive = isActive
        });

    // ── User not found ────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenUserNotFound_ReturnsNotFound()
    {
        var result = await _sut.GetUserRoleAssignmentsAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    // ── Zero roles is a valid answer, not an error ─────────────────────────────

    [Fact]
    public async Task WhenUserHasNoRoles_ReturnsEmptyList()
    {
        // The state a freshly created user is in — must not read as an error.
        var user = await PersistAsync(CreateTestUser("orphan"));

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    // ── Single tenant ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SingleTenantSingleRole_ReturnsExactTenantRoleAndName()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        await AssignAsync(user.Id, role.Id, tenant.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        Assert.True(result.IsSuccess);
        var dto = Assert.Single(result.Value);
        Assert.Equal(tenant.Id, dto.TenantId);
        Assert.Equal(role.Id, dto.RoleId);
        Assert.Equal("Admin", dto.RoleName);
    }

    [Fact]
    public async Task SingleTenantMultipleRoles_ReturnsAllForThatTenant()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var admin = await PersistAsync(CreateTestRole("Admin"));
        var operatorRole = await PersistAsync(CreateTestRole("Operator"));
        await AssignAsync(user.Id, admin.Id, tenant.Id);
        await AssignAsync(user.Id, operatorRole.Id, tenant.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        Assert.Equal(2, result.Value.Count);
        Assert.All(result.Value, dto => Assert.Equal(tenant.Id, dto.TenantId));
        Assert.Contains(result.Value, dto => dto.RoleName == "Admin");
        Assert.Contains(result.Value, dto => dto.RoleName == "Operator");
    }

    // ── Multi-tenant: the case the flat list cannot express ───────────────────

    [Fact]
    public async Task MultipleTenants_KeepsAssignmentsDistinctPerTenant()
    {
        // Tenant A -> Admin + Operator, Tenant B -> Viewer.
        // UserDto.Roles would flatten this to ["Admin","Operator","Viewer"] with no
        // way to tell that Viewer belongs to B — the whole reason for this endpoint.
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var admin = await PersistAsync(CreateTestRole("Admin"));
        var operatorRole = await PersistAsync(CreateTestRole("Operator"));
        var viewer = await PersistAsync(CreateTestRole("Viewer"));

        await AssignAsync(user.Id, admin.Id, tenantA.Id);
        await AssignAsync(user.Id, operatorRole.Id, tenantA.Id);
        await AssignAsync(user.Id, viewer.Id, tenantB.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        Assert.Equal(3, result.Value.Count);
        Assert.Contains(result.Value, d => d.TenantId == tenantA.Id && d.RoleName == "Admin");
        Assert.Contains(result.Value, d => d.TenantId == tenantA.Id && d.RoleName == "Operator");
        Assert.Contains(result.Value, d => d.TenantId == tenantB.Id && d.RoleName == "Viewer");

        // Tenant B must expose exactly one role, not inherit tenant A's.
        Assert.Single(result.Value, d => d.TenantId == tenantB.Id);
    }

    [Fact]
    public async Task SameRoleInTwoTenants_ReturnsTwoDistinctRows()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var admin = await PersistAsync(CreateTestRole("Admin"));

        await AssignAsync(user.Id, admin.Id, tenantA.Id);
        await AssignAsync(user.Id, admin.Id, tenantB.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        // Must NOT be collapsed by role name.
        Assert.Equal(2, result.Value.Count);
        Assert.All(result.Value, d => Assert.Equal(admin.Id, d.RoleId));
        Assert.Contains(result.Value, d => d.TenantId == tenantA.Id);
        Assert.Contains(result.Value, d => d.TenantId == tenantB.Id);
    }

    [Fact]
    public async Task ReturnsOnlyTheRequestedUsersAssignments()
    {
        var alice = await PersistAsync(CreateTestUser("alice"));
        var bob = await PersistAsync(CreateTestUser("bob"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var admin = await PersistAsync(CreateTestRole("Admin"));
        var viewer = await PersistAsync(CreateTestRole("Viewer"));

        await AssignAsync(alice.Id, admin.Id, tenant.Id);
        await AssignAsync(bob.Id, viewer.Id, tenant.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(alice.Id);

        var dto = Assert.Single(result.Value);
        Assert.Equal(admin.Id, dto.RoleId);
    }

    // ── IsActive ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExcludesInactiveAssignments()
    {
        // Matches UserRepository.GetPermissionCodesForUserAsync, which filters
        // ur.IsActive when resolving what a user can actually do. A read that
        // disagreed would show roles granting nothing — and a caller replacing
        // roles from that list would silently reactivate them.
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var active = await PersistAsync(CreateTestRole("Admin"));
        var inactive = await PersistAsync(CreateTestRole("Viewer"));

        await AssignAsync(user.Id, active.Id, tenant.Id);
        await AssignAsync(user.Id, inactive.Id, tenant.Id, isActive: false);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        var dto = Assert.Single(result.Value);
        Assert.Equal("Admin", dto.RoleName);
    }

    // ── Ordering ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrdersByTenantThenRoleName()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var zulu = await PersistAsync(CreateTestRole("Zulu"));
        var alpha = await PersistAsync(CreateTestRole("Alpha"));

        // Inserted out of order; the contract promises a deterministic result.
        await AssignAsync(user.Id, zulu.Id, tenant.Id);
        await AssignAsync(user.Id, alpha.Id, tenant.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        Assert.Equal(new[] { "Alpha", "Zulu" }, result.Value.Select(d => d.RoleName));
    }

    // ── Membership is not a filter (T8) ───────────────────────────────────────

    [Fact]
    public async Task ReportsAssignmentsEvenWithoutTenantMembership()
    {
        // There is a known separate gap: removing a UserTenant leaves that tenant's
        // AppUserRole rows behind. This read reports what is persisted rather than
        // hiding the inconsistency, so the orphan stays visible instead of silently
        // disappearing.
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        await AssignAsync(user.Id, role.Id, tenant.Id); // no UserTenant row at all

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        var dto = Assert.Single(result.Value);
        Assert.Equal(tenant.Id, dto.TenantId);
    }
}
