using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// CT-4 remediation, phase P5 — the S1 question, finally answerable.
///
/// <para>S1 asked whether <c>POST /users/{id}/roles</c> validates the CALLER's authority
/// over the tenant named in the request body, or merely the TARGET's membership of it.
/// CT-4 could not answer it: the dev database had no Tenant B members, so every
/// cross-tenant attempt was refused by the target check and told us nothing about the
/// actor check. A denial proved nothing.</para>
///
/// <para>These tests remove that ambiguity. Cross-tenant cases use a target that
/// genuinely belongs to Tenant B, so the target check <b>would pass</b>; the request is
/// refused only because the actor gate fires first. And the two gates return different
/// error codes, so a test can assert which one fired rather than merely that something
/// failed.</para>
/// </summary>
public class UserService_RoleAssignmentActorAuthorityTests : RelationalIdentityTestBase
{
    private readonly UserService _sut;

    public UserService_RoleAssignmentActorAuthorityTests()
    {
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork, TenantProvider);
    }

    private const string ActorError = "Role.TenantNotAuthorized";
    private const string TargetError = "User.NotInTenant";

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

    private Task<List<AppUserRole>> RowsAsync(Guid userId) =>
        Context.Set<AppUserRole>().AsNoTracking().Where(ur => ur.UserId == userId).ToListAsync();

    // ── S1-1 — the permitted case ─────────────────────────────────────────────

    [Fact]
    public async Task S1_1_ActorInTenantA_AssigningInTenantA_IsAllowed()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);
        var role = await PersistAsync(CreateTestRole("Admin"));

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { role.Id }));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(await RowsAsync(target.Id));
        Assert.Equal(a.Id, row.TenantId);
        Assert.Equal(role.Id, row.RoleId);
    }

    // ── S1-2 — the decisive test ──────────────────────────────────────────────

    [Fact]
    public async Task S1_2_ActorInTenantA_AssigningInTenantB_IsDeniedByActorGate()
    {
        // The target is a REAL active member of Tenant B, so the target check would
        // succeed. The only thing that can refuse this request is the actor gate.
        var (_, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);
        var role = await PersistAsync(CreateTestRole("Admin"));

        Assert.True(await Users.IsAssignedToTenantAsync(target.Id, b.Id));

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(b.Id, new[] { role.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal(ActorError, result.Error.Code);
    }

    // ── S1-3 — target gate still works ────────────────────────────────────────

    [Fact]
    public async Task S1_3_ActorInTenantA_TargetOutsideTenantA_IsDeniedByTargetGate()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id); // not in A
        var role = await PersistAsync(CreateTestRole("Admin"));

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { role.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal(TargetError, result.Error.Code);
    }

    // ── S1-4 — the gates are distinguishable ──────────────────────────────────

    [Fact]
    public async Task S1_4_ActorAndTargetDenialsUseDifferentErrorCodes()
    {
        // Without this, S1-2 could be passing for the target reason and nobody would
        // know — the precise flaw that made CT-4's T8 non-probative.
        var (a, b) = await TwoTenantsAsync();
        var inB = await MemberOfAsync("bob", b.Id);
        var role = await PersistAsync(CreateTestRole("Admin"));

        var actor = await _sut.AssignRolesAsync(
            inB.Id, new AssignRolesRequest(b.Id, new[] { role.Id }));
        var target = await _sut.AssignRolesAsync(
            inB.Id, new AssignRolesRequest(a.Id, new[] { role.Id }));

        Assert.Equal(ActorError, actor.Error.Code);
        Assert.Equal(TargetError, target.Error.Code);
        Assert.NotEqual(actor.Error.Code, target.Error.Code);
    }

    // ── S1-5 / S1-6 — denials mutate nothing ──────────────────────────────────

    [Fact]
    public async Task S1_5_ActorDenial_LeavesTenantBRolesUnchanged()
    {
        var (_, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);
        var existing = await PersistAsync(CreateTestRole("Viewer"));
        var attempted = await PersistAsync(CreateTestRole("Admin"));
        await GrantAsync(target.Id, existing.Id, b.Id);

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(b.Id, new[] { attempted.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal(ActorError, result.Error.Code);

        Context.ChangeTracker.Clear();
        var row = Assert.Single(await RowsAsync(target.Id));
        Assert.Equal(existing.Id, row.RoleId); // not replaced by the attempted role
        Assert.Equal(b.Id, row.TenantId);
    }

    [Fact]
    public async Task S1_6_TargetDenial_LeavesRoleStateUnchanged()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);
        var existing = await PersistAsync(CreateTestRole("Viewer"));
        var attempted = await PersistAsync(CreateTestRole("Admin"));
        await GrantAsync(target.Id, existing.Id, b.Id);

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { attempted.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal(TargetError, result.Error.Code);

        Context.ChangeTracker.Clear();
        var row = Assert.Single(await RowsAsync(target.Id));
        Assert.Equal(existing.Id, row.RoleId);
    }

    // ── S1-7 / S1-8 — multi-tenant target ─────────────────────────────────────

    [Fact]
    public async Task S1_7_MultiTenantTarget_ActorMayAssignOnlyWithinTenantA()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var role = await PersistAsync(CreateTestRole("Admin"));

        var allowed = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { role.Id }));
        var refused = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(b.Id, new[] { role.Id }));

        Assert.True(allowed.IsSuccess);
        Assert.True(refused.IsFailure);
        Assert.Equal(ActorError, refused.Error.Code);
    }

    [Fact]
    public async Task S1_8_TenantBRolesSurviveATenantAAssignment()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var viewer = await PersistAsync(CreateTestRole("Viewer"));
        var admin = await PersistAsync(CreateTestRole("Admin"));
        await GrantAsync(target.Id, viewer.Id, b.Id);

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { admin.Id }));

        Assert.True(result.IsSuccess);

        Context.ChangeTracker.Clear();
        var rows = await RowsAsync(target.Id);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.TenantId == b.Id && r.RoleId == viewer.Id);
        Assert.Contains(rows, r => r.TenantId == a.Id && r.RoleId == admin.Id);
    }

    // ── S1-9 / S1-10 — target membership edge cases ───────────────────────────

    [Fact]
    public async Task S1_9_InactiveMembershipInTenantA_IsDeniedByTargetGate()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("lapsed"));
        await PersistAsync(CreateMembership(target.Id, a.Id, isActive: false));
        var role = await PersistAsync(CreateTestRole("Admin"));

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { role.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal(TargetError, result.Error.Code);
    }

    [Fact]
    public async Task S1_10_TenantLessTarget_IsDeniedByTargetGate()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("nomad"));
        var role = await PersistAsync(CreateTestRole("Admin"));

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { role.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal(TargetError, result.Error.Code);
    }

    // ── S1-11 / S1-12 — existing contract preserved ───────────────────────────

    [Fact]
    public async Task S1_11_ReassigningTheSameRole_IsIdempotentInEffect()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);
        var role = await PersistAsync(CreateTestRole("Admin"));

        Assert.True((await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { role.Id }))).IsSuccess);
        Assert.True((await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { role.Id }))).IsSuccess);

        Context.ChangeTracker.Clear();
        var row = Assert.Single(await RowsAsync(target.Id));
        Assert.Equal(role.Id, row.RoleId);
    }

    [Fact]
    public async Task S1_12_UnknownRoleId_StillFailsWithoutPartialMutation()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);
        var existing = await PersistAsync(CreateTestRole("Viewer"));
        await GrantAsync(target.Id, existing.Id, a.Id);

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { existing.Id, Guid.NewGuid() }));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);

        Context.ChangeTracker.Clear();
        var row = Assert.Single(await RowsAsync(target.Id));
        Assert.Equal(existing.Id, row.RoleId);
    }

    // ── Role tenancy — AppRole.TenantId is nullable by design ─────────────────

    [Fact]
    public async Task SystemRole_WithNullTenantId_IsAssignableInAnyTenant()
    {
        // CreateTestRole leaves TenantId null, i.e. a system role. This is the
        // behaviour every seeded role relies on and must not regress.
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);
        var systemRole = await PersistAsync(CreateTestRole("Admin"));

        Assert.Null(systemRole.TenantId);

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { systemRole.Id }));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RoleOwnedByTenantB_IsNotAssignableInTenantA()
    {
        // A custom role belonging to Tenant B must not be grantable inside Tenant A.
        // It reports as NotFound rather than a distinct error so Tenant A cannot use
        // this endpoint to discover which custom roles Tenant B has defined.
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var tenantBRole = CreateTestRole("BespokeB");
        tenantBRole.TenantId = b.Id;
        await PersistAsync(tenantBRole);

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { tenantBRole.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code);

        Context.ChangeTracker.Clear();
        Assert.Empty(await RowsAsync(target.Id));
    }

    [Fact]
    public async Task RoleOwnedByCurrentTenant_IsAssignable()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var tenantARole = CreateTestRole("BespokeA");
        tenantARole.TenantId = a.Id;
        await PersistAsync(tenantARole);

        var result = await _sut.AssignRolesAsync(
            target.Id, new AssignRolesRequest(a.Id, new[] { tenantARole.Id }));

        Assert.True(result.IsSuccess);
    }

    // ── Fixture integrity ─────────────────────────────────────────────────────

    [Fact]
    public async Task Fixture_TenantBTargetGenuinelyBelongsToTenantB()
    {
        // The guard for S1-2. If this fails, S1-2's denial could be the target gate
        // firing rather than the actor gate, and the whole S1 answer would be void.
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        Assert.True(await Users.IsAssignedToTenantAsync(target.Id, b.Id));
        Assert.False(await Users.IsAssignedToTenantAsync(target.Id, a.Id));
    }
}
