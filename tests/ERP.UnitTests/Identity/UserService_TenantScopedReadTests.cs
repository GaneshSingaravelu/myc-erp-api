using ERP.Identity.Entities;
using ERP.Identity.Services;

namespace ERP.UnitTests.Identity;

/// <summary>
/// CT-4 remediation, phase P2 — tenant scoping of the Users read endpoints.
///
/// CT-4 confirmed at runtime that a Tenant A administrator could read a user outside
/// Tenant A (F1) and that the list returned every user in the workspace (F3). These
/// tests pin the corrected behaviour.
///
/// <para><b>Why relational (SQLite) rather than InMemory.</b> Scoping is expressed as
/// an EXISTS subquery over UserTenants plus a filtered Include over UserRoles.
/// InMemory does not model those the way a real provider does, which is the same
/// reason UserService_AssignRolesRelationalTests exists.</para>
///
/// <para><b>Why a genuine Tenant B.</b> CT-4's runtime attempt could not distinguish
/// "denied because the caller lacks authority" from "denied because the target was in
/// no tenant", since the dev database had zero Tenant B members. Every negative case
/// below uses a target that is a real, active member of Tenant B, so a pass means
/// scoping worked rather than that the fixture was empty.</para>
///
/// <para>Every test sets the acting tenant explicitly. Relying on the base default
/// would let a scoping assertion pass without exercising any scoping.</para>
/// </summary>
public class UserService_TenantScopedReadTests : RelationalIdentityTestBase
{
    private readonly UserService _sut;

    public UserService_TenantScopedReadTests()
    {
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork, TenantProvider);
    }

    /// <summary>Tenant A and Tenant B, with the caller acting inside Tenant A.</summary>
    private async Task<(Tenant A, Tenant B)> TwoTenantsAsync()
    {
        var a = await PersistAsync(CreateTestTenant("AAA"));
        var b = await PersistAsync(CreateTestTenant("BBB"));
        SetCurrentTenant(a.Id, "AAA");
        return (a, b);
    }

    private async Task<AppUser> MemberOfAsync(string name, params Guid[] tenantIds)
    {
        var user = await PersistAsync(CreateTestUser(name));
        foreach (var tenantId in tenantIds)
            await PersistAsync(CreateMembership(user.Id, tenantId));
        return user;
    }

    private async Task GrantAsync(Guid userId, Guid roleId, Guid tenantId) =>
        await PersistAsync(new AppUserRole
        {
            UserId = userId,
            RoleId = roleId,
            TenantId = tenantId,
            IsActive = true
        });

    // ── GET /users/{id} ───────────────────────────────────────────────────────

    [Fact] // R3
    public async Task GetById_TenantATarget_IsVisibleToTenantA()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var result = await _sut.GetByIdAsync(target.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("alice", result.Value.UserName);
    }

    [Fact] // R4 — the F1 regression test
    public async Task GetById_GenuineTenantBTarget_IsNotFoundForTenantA()
    {
        var (_, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        var result = await _sut.GetByIdAsync(target.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact] // R5
    public async Task GetById_TenantLessTarget_IsNotFound()
    {
        await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("nomad"));

        var result = await _sut.GetByIdAsync(target.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact] // R6
    public async Task GetById_InactiveMembershipInTenantA_IsNotFound()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("lapsed"));
        await PersistAsync(CreateMembership(target.Id, a.Id, isActive: false));

        var result = await _sut.GetByIdAsync(target.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact] // R10
    public async Task GetById_MultiTenantTarget_IsVisibleFromTenantA()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);

        var result = await _sut.GetByIdAsync(target.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("dual", result.Value.UserName);
    }

    [Fact]
    public async Task GetById_MultiTenantTarget_ExposesOnlyTenantARoleNames()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var adminRole = await PersistAsync(CreateTestRole("Admin"));
        var viewerRole = await PersistAsync(CreateTestRole("Viewer"));
        await GrantAsync(target.Id, adminRole.Id, a.Id);
        await GrantAsync(target.Id, viewerRole.Id, b.Id);

        var result = await _sut.GetByIdAsync(target.Id);

        // UserDto.Roles is a flat name list; it must not carry the Tenant B role.
        Assert.Equal(new[] { "Admin" }, result.Value.Roles);
        Assert.DoesNotContain("Viewer", result.Value.Roles);
    }

    // ── GET /users ────────────────────────────────────────────────────────────

    [Fact] // R7 + R8 + R9
    public async Task GetAll_ReturnsOnlyActiveTenantAMembers()
    {
        var (a, b) = await TwoTenantsAsync();
        await MemberOfAsync("alice", a.Id);
        await MemberOfAsync("dual", a.Id, b.Id);
        await MemberOfAsync("bob", b.Id);
        await PersistAsync(CreateTestUser("nomad"));
        var lapsed = await PersistAsync(CreateTestUser("lapsed"));
        await PersistAsync(CreateMembership(lapsed.Id, a.Id, isActive: false));

        var result = await _sut.GetAllAsync();

        var names = result.Value.Select(u => u.UserName).ToArray();
        Assert.Equal(new[] { "alice", "dual" }, names);
        Assert.DoesNotContain("bob", names);
        Assert.DoesNotContain("nomad", names);
        Assert.DoesNotContain("lapsed", names);
    }

    [Fact]
    public async Task GetAll_MultiTenantMemberAppearsExactlyOnce()
    {
        // EXISTS rather than a join: two memberships must not fan out into two rows.
        var (a, b) = await TwoTenantsAsync();
        await MemberOfAsync("dual", a.Id, b.Id);

        var result = await _sut.GetAllAsync();

        Assert.Single(result.Value, u => u.UserName == "dual");
    }

    [Fact]
    public async Task GetAll_DoesNotLeakOtherTenantRoleNames()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var adminRole = await PersistAsync(CreateTestRole("Admin"));
        var viewerRole = await PersistAsync(CreateTestRole("Viewer"));
        await GrantAsync(target.Id, adminRole.Id, a.Id);
        await GrantAsync(target.Id, viewerRole.Id, b.Id);

        var result = await _sut.GetAllAsync();

        var dto = Assert.Single(result.Value, u => u.UserName == "dual");
        Assert.Equal(new[] { "Admin" }, dto.Roles);
    }

    // ── GET /users/{id}/roles ─────────────────────────────────────────────────

    [Fact] // R11
    public async Task GetRoles_MultiTenantTarget_ReturnsTenantARolesOnly()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var adminRole = await PersistAsync(CreateTestRole("Admin"));
        var viewerRole = await PersistAsync(CreateTestRole("Viewer"));
        await GrantAsync(target.Id, adminRole.Id, a.Id);
        await GrantAsync(target.Id, viewerRole.Id, b.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(target.Id);

        var dto = Assert.Single(result.Value);
        Assert.Equal(a.Id, dto.TenantId);
        Assert.Equal("Admin", dto.RoleName);
        Assert.DoesNotContain(result.Value, d => d.TenantId == b.Id);
    }

    [Fact] // R13
    public async Task GetRoles_GenuineTenantBTarget_IsNotFoundForTenantA()
    {
        var (_, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);
        var role = await PersistAsync(CreateTestRole("Viewer"));
        await GrantAsync(target.Id, role.Id, b.Id);

        var result = await _sut.GetUserRoleAssignmentsAsync(target.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    // ── GET /users/{id}/tenants ───────────────────────────────────────────────

    [Fact] // R12 — the tightened contract
    public async Task GetTenants_MultiTenantTarget_ReturnsTenantAMembershipOnly()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);

        var result = await _sut.GetUserTenantsAsync(target.Id);

        var dto = Assert.Single(result.Value);
        Assert.Equal(a.Id, dto.TenantId);
        Assert.Equal("aaa", dto.TenantCode);

        // Tenant B's existence, id, code and name are all withheld.
        Assert.DoesNotContain(result.Value, t => t.TenantId == b.Id);
        Assert.DoesNotContain(result.Value, t => t.TenantCode == "bbb");
    }

    [Fact] // R14
    public async Task GetTenants_GenuineTenantBTarget_IsNotFoundForTenantA()
    {
        var (_, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        var result = await _sut.GetUserTenantsAsync(target.Id);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    // ── Fixture integrity ─────────────────────────────────────────────────────

    [Fact]
    public async Task Fixture_TenantBTargetGenuinelyBelongsToTenantB()
    {
        // Guards every negative test above. If this fails, those tests would be
        // denying for the wrong reason — the exact flaw that made CT-4's T8 vacuous.
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        Assert.True(await Users.IsAssignedToTenantAsync(target.Id, b.Id));
        Assert.False(await Users.IsAssignedToTenantAsync(target.Id, a.Id));
    }
}
