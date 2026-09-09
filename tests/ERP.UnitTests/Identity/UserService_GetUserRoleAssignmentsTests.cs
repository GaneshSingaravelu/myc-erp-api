using ERP.Identity.Entities;
using Moq;
using ERP.Application.Abstractions.MultiTenancy;
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
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork, TenantProvider);
    }

    /// <summary>
    /// Put the service in <paramref name="tenantId"/>'s context and make the target an
    /// active member of it. Since P2 both are required: the context decides what the
    /// caller may see, the membership decides whether the target is in scope at all.
    /// Every test sets this explicitly — relying on a default tenant would let a
    /// scoping assertion pass without ever exercising the scoping.
    /// </summary>
    private async Task ActingInAsync(Guid tenantId, Guid targetUserId)
    {
        SetCurrentTenant(tenantId);
        await PersistAsync(CreateMembership(targetUserId, tenantId));
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
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        await ActingInAsync(tenant.Id, user.Id);

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
        await ActingInAsync(tenant.Id, user.Id);
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
        await ActingInAsync(tenant.Id, user.Id);
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
    public async Task MultiTenantUser_ReturnsOnlyActingTenantsAssignments()
    {
        // Tenant A -> Admin + Operator, Tenant B -> Viewer.
        //
        // Before the CT-4 remediation this returned all three rows, so a Tenant A
        // administrator could see that the user also held Viewer in Tenant B. Tenant
        // identity per row is still preserved (that is why the endpoint exists), but
        // the visible set is now the acting tenant's alone.
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var admin = await PersistAsync(CreateTestRole("Admin"));
        var operatorRole = await PersistAsync(CreateTestRole("Operator"));
        var viewer = await PersistAsync(CreateTestRole("Viewer"));

        // Genuinely a member of BOTH — so exclusion below is caused by scoping,
        // not by the user being absent from Tenant B.
        await PersistAsync(CreateMembership(user.Id, tenantB.Id));
        await ActingInAsync(tenantA.Id, user.Id);

        await AssignAsync(user.Id, admin.Id, tenantA.Id);
        await AssignAsync(user.Id, operatorRole.Id, tenantA.Id);
        await AssignAsync(user.Id, viewer.Id, tenantB.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, d => d.TenantId == tenantA.Id && d.RoleName == "Admin");
        Assert.Contains(result.Value, d => d.TenantId == tenantA.Id && d.RoleName == "Operator");

        // The security assertion: Tenant B's row is not disclosed to Tenant A.
        Assert.DoesNotContain(result.Value, d => d.TenantId == tenantB.Id);
        Assert.DoesNotContain(result.Value, d => d.RoleName == "Viewer");
    }

    [Fact]
    public async Task SameRoleInTwoTenants_ReturnsOnlyActingTenantsRow()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenantA = await PersistAsync(CreateTestTenant("AAA"));
        var tenantB = await PersistAsync(CreateTestTenant("BBB"));
        var admin = await PersistAsync(CreateTestRole("Admin"));

        await PersistAsync(CreateMembership(user.Id, tenantB.Id));
        await ActingInAsync(tenantA.Id, user.Id);

        await AssignAsync(user.Id, admin.Id, tenantA.Id);
        await AssignAsync(user.Id, admin.Id, tenantB.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        // Identical role name in both tenants: the row that comes back must be the
        // acting tenant's, and the count must not silently include the other one.
        var dto = Assert.Single(result.Value);
        Assert.Equal(admin.Id, dto.RoleId);
        Assert.Equal(tenantA.Id, dto.TenantId);
    }

    [Fact]
    public async Task ReturnsOnlyTheRequestedUsersAssignments()
    {
        var alice = await PersistAsync(CreateTestUser("alice"));
        var bob = await PersistAsync(CreateTestUser("bob"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var admin = await PersistAsync(CreateTestRole("Admin"));
        var viewer = await PersistAsync(CreateTestRole("Viewer"));

        await ActingInAsync(tenant.Id, alice.Id);
        await PersistAsync(CreateMembership(bob.Id, tenant.Id));
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

        await ActingInAsync(tenant.Id, user.Id);
        await AssignAsync(user.Id, active.Id, tenant.Id);
        await AssignAsync(user.Id, inactive.Id, tenant.Id, isActive: false);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        var dto = Assert.Single(result.Value);
        Assert.Equal("Admin", dto.RoleName);
    }

    // ── Ordering ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OrdersByRoleNameWithinActingTenant()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var zulu = await PersistAsync(CreateTestRole("Zulu"));
        var alpha = await PersistAsync(CreateTestRole("Alpha"));

        // Inserted out of order; the contract promises a deterministic result.
        await ActingInAsync(tenant.Id, user.Id);
        await AssignAsync(user.Id, zulu.Id, tenant.Id);
        await AssignAsync(user.Id, alpha.Id, tenant.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        // Ordering is now within one tenant, so role name is the whole key.
        Assert.Equal(new[] { "Alpha", "Zulu" }, result.Value.Select(d => d.RoleName));
    }

    // ── Membership IS the gate (CT-4 remediation) ─────────────────────────────

    [Fact]
    public async Task WithoutTenantMembership_ReturnsNotFound()
    {
        // This assertion is deliberately inverted from its pre-remediation form.
        //
        // It used to prove the opposite: that orphaned AppUserRole rows left behind
        // when a UserTenant is deleted stayed VISIBLE, so the inconsistency could not
        // hide. Membership is now an authorization gate, so those orphans are no
        // longer surfaced here. The underlying orphan-cleanup gap is unchanged and
        // still needs its own fix — this endpoint simply stops being the thing that
        // reveals it. Recorded in the CP2 report as a consequence of the remediation.
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        await AssignAsync(user.Id, role.Id, tenant.Id); // no UserTenant row at all

        SetCurrentTenant(tenant.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(user.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }
}
