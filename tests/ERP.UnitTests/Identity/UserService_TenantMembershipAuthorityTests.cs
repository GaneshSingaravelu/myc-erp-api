using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// CT-4 remediation, phase P6 — membership management confined to the caller's tenant.
///
/// <para>This closes the live data-loss path found at CP4A. The tenant-scoped read shows
/// a Tenant A administrator only the Tenant A membership; the old replace-all write then
/// treated that visible set as the user's complete membership. Opening the Tenants tab
/// on an A+B user and pressing Save destroyed B. The frontend round-trip is asserted
/// directly below (M2/M18).</para>
///
/// <para>Attachment and ejection are both out of scope by owner decision, so the only
/// request that survives validation describes state that already exists — a valid call
/// is a verified no-op. Most of these tests therefore assert what did <b>not</b> happen,
/// which is exactly the point.</para>
///
/// <para>Every cross-tenant case uses a target with a real active Tenant B membership,
/// so a denial cannot be an artefact of an empty fixture.</para>
/// </summary>
public class UserService_TenantMembershipAuthorityTests : RelationalIdentityTestBase
{
    private readonly UserService _sut;

    public UserService_TenantMembershipAuthorityTests()
    {
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork, TenantProvider);
    }

    private const string BodyAuthorityError = "Tenant.NotAuthorized";
    private const string EmptySelectionError = "Tenant.SelectionRequired";
    private const string OutOfScopeError = "User.NotFound";

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

    private Task<List<UserTenant>> MembershipsAsync(Guid userId) =>
        Context.Set<UserTenant>().AsNoTracking().Where(ut => ut.UserId == userId).ToListAsync();

    private static AssignTenantsRequest Save(params Guid[] tenantIds) => new(tenantIds);

    // ── M1 / M2 / M18 — the permitted save ────────────────────────────────────

    [Fact] // M1
    public async Task M1_AOnlyTarget_SavingA_Succeeds()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var result = await _sut.AssignTenantsAsync(target.Id, Save(a.Id));

        Assert.True(result.IsSuccess);
        var membership = Assert.Single(await MembershipsAsync(target.Id));
        Assert.Equal(a.Id, membership.TenantId);
    }

    [Fact] // M2 + M18 — the exact frontend round-trip that used to destroy B
    public async Task M2_MultiTenantTarget_SavingOnlyA_PreservesB()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);

        // What GET /users/{id}/tenants now shows Admin A, sent straight back.
        var result = await _sut.AssignTenantsAsync(target.Id, Save(a.Id));

        Assert.True(result.IsSuccess);

        Context.ChangeTracker.Clear();
        var memberships = await MembershipsAsync(target.Id);

        Assert.Equal(2, memberships.Count); // M18
        Assert.Contains(memberships, ut => ut.TenantId == a.Id);
        Assert.Contains(memberships, ut => ut.TenantId == b.Id);
    }

    [Fact] // M3
    public async Task M3_MultiTenantTarget_SavingA_LeavesBIsDefaultUnchanged()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("dual"));
        await PersistAsync(new UserTenant
        {
            UserId = target.Id, TenantId = a.Id,
            IsDefault = false, IsActive = true, JoinedAt = DateTime.UtcNow
        });
        await PersistAsync(new UserTenant
        {
            UserId = target.Id, TenantId = b.Id,
            IsDefault = true, IsActive = true, JoinedAt = DateTime.UtcNow
        });

        Assert.True((await _sut.AssignTenantsAsync(target.Id, Save(a.Id))).IsSuccess);

        Context.ChangeTracker.Clear();
        var memberships = await MembershipsAsync(target.Id);

        // B is still the default; A was not promoted behind the other tenant's back.
        Assert.True(memberships.Single(ut => ut.TenantId == b.Id).IsDefault);
        Assert.False(memberships.Single(ut => ut.TenantId == a.Id).IsDefault);
    }

    [Fact] // M4
    public async Task M4_MultiTenantTarget_SavingA_LeavesBRolesUnchanged()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var viewer = await PersistAsync(CreateTestRole("Viewer"));
        await PersistAsync(new AppUserRole
        {
            UserId = target.Id, RoleId = viewer.Id, TenantId = b.Id, IsActive = true
        });

        Assert.True((await _sut.AssignTenantsAsync(target.Id, Save(a.Id))).IsSuccess);

        Context.ChangeTracker.Clear();
        var bRoles = await Users.GetUserRoleAssignmentsInTenantAsync(target.Id, b.Id);
        var role = Assert.Single(bRoles);
        Assert.Equal(viewer.Id, role.RoleId);
    }

    [Fact] // M15 — the row itself must not be churned
    public async Task M15_UnchangedSave_DoesNotAlterEitherRowsStoredValues()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);

        var before = (await MembershipsAsync(target.Id))
            .ToDictionary(ut => ut.TenantId, ut => (ut.Id, ut.IsDefault, ut.IsActive, ut.JoinedAt));

        Assert.True((await _sut.AssignTenantsAsync(target.Id, Save(a.Id))).IsSuccess);

        Context.ChangeTracker.Clear();
        var after = (await MembershipsAsync(target.Id))
            .ToDictionary(ut => ut.TenantId, ut => (ut.Id, ut.IsDefault, ut.IsActive, ut.JoinedAt));

        // Same row identities — not deleted and recreated — and identical values.
        Assert.Equal(before.Count, after.Count);
        foreach (var (tenantId, value) in before)
            Assert.Equal(value, after[tenantId]);
    }

    // ── M5 / M6 / M13 — foreign tenant ids in the body ────────────────────────

    [Fact] // M5 + M13
    public async Task M5_BodyNamingBothTenants_IsDeniedAndMutatesNothing()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var before = await MembershipsAsync(target.Id);

        var result = await _sut.AssignTenantsAsync(target.Id, Save(a.Id, b.Id));

        Assert.True(result.IsFailure);
        Assert.Equal(BodyAuthorityError, result.Error.Code);

        Context.ChangeTracker.Clear();
        var after = await MembershipsAsync(target.Id);
        Assert.Equal(before.Count, after.Count);
        Assert.Contains(after, ut => ut.TenantId == a.Id);
        Assert.Contains(after, ut => ut.TenantId == b.Id);
    }

    [Fact] // M6
    public async Task M6_BodyNamingOnlyTheOtherTenant_IsDeniedAndMutatesNothing()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);

        var result = await _sut.AssignTenantsAsync(target.Id, Save(b.Id));

        Assert.True(result.IsFailure);
        Assert.Equal(BodyAuthorityError, result.Error.Code);

        Context.ChangeTracker.Clear();
        Assert.Equal(2, (await MembershipsAsync(target.Id)).Count);
    }

    [Fact] // M13 — the foreign id is refused, never quietly dropped
    public async Task M13_ForeignIdIsRefusedRatherThanFilteredOut()
    {
        // If [A, B] were silently reduced to [A], the caller would be told their
        // request succeeded when half of it was never permitted.
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);

        var result = await _sut.AssignTenantsAsync(target.Id, Save(a.Id, b.Id));

        Assert.True(result.IsFailure);
        Assert.NotEqual("Tenant.NotFound", result.Error.Code);
    }

    // ── M7 / M8 / M9 — out-of-scope targets: no attachment ────────────────────

    [Fact] // M7
    public async Task M7_InactiveMembershipTarget_IsOutOfScopeAndNotReactivated()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("lapsed"));
        await PersistAsync(CreateMembership(target.Id, a.Id, isActive: false));

        var result = await _sut.AssignTenantsAsync(target.Id, Save(a.Id));

        Assert.True(result.IsFailure);
        Assert.Equal(OutOfScopeError, result.Error.Code);

        Context.ChangeTracker.Clear();
        var membership = Assert.Single(await MembershipsAsync(target.Id));
        Assert.False(membership.IsActive); // still inactive
    }

    [Fact] // M8
    public async Task M8_TenantLessTarget_CannotBeAttached()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("nomad"));

        var result = await _sut.AssignTenantsAsync(target.Id, Save(a.Id));

        Assert.True(result.IsFailure);
        Assert.Equal(OutOfScopeError, result.Error.Code);

        Context.ChangeTracker.Clear();
        Assert.Empty(await MembershipsAsync(target.Id));
    }

    [Fact] // M9 + M16
    public async Task M9_GenuineTenantBOnlyTarget_CannotBeAttachedToTenantA()
    {
        // The escalation this decision forbids: attach a foreign identity, then read
        // it through the endpoints the remediation just secured.
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        var result = await _sut.AssignTenantsAsync(target.Id, Save(a.Id));

        Assert.True(result.IsFailure);
        Assert.Equal(OutOfScopeError, result.Error.Code);

        Context.ChangeTracker.Clear();
        var membership = Assert.Single(await MembershipsAsync(target.Id));
        Assert.Equal(b.Id, membership.TenantId);
        Assert.False(await Users.IsAssignedToTenantAsync(target.Id, a.Id));
    }

    // ── M16 / M17 — no enumeration oracle ─────────────────────────────────────

    [Fact] // M16 + M17
    public async Task M17_UnknownAndForeignTargetsAreIndistinguishable()
    {
        // A Tenant B member and a user id that does not exist must produce the same
        // answer, or the endpoint reveals that an id it refuses is nonetheless real.
        var (a, b) = await TwoTenantsAsync();
        var inB = await MemberOfAsync("bob", b.Id);

        var foreign = await _sut.AssignTenantsAsync(inB.Id, Save(a.Id));
        var unknown = await _sut.AssignTenantsAsync(Guid.NewGuid(), Save(a.Id));

        Assert.True(foreign.IsFailure);
        Assert.True(unknown.IsFailure);
        Assert.Equal(unknown.Error.Code, foreign.Error.Code);
        Assert.DoesNotContain("BBB", foreign.Error.Message);
        Assert.DoesNotContain(b.Id.ToString(), foreign.Error.Message);
    }

    // ── M10 — the P4A create round-trip ───────────────────────────────────────

    [Fact] // M10 + M14
    public async Task M10_NewlyCreatedUser_SavingA_StaysSingleMembershipAndDefault()
    {
        var (a, _) = await TwoTenantsAsync();

        var created = await _sut.CreateAsync(
            new CreateUserRequest("newuser", "new@example.com", "Password@123", "New", "User"));
        Assert.True(created.IsSuccess);

        var result = await _sut.AssignTenantsAsync(created.Value.Id, Save(a.Id));

        Assert.True(result.IsSuccess);

        Context.ChangeTracker.Clear();
        var membership = Assert.Single(await MembershipsAsync(created.Value.Id)); // M14
        Assert.Equal(a.Id, membership.TenantId);
        Assert.True(membership.IsDefault);
        Assert.True(membership.IsActive);
    }

    // ── M11 / M12 — the empty list ────────────────────────────────────────────

    [Fact] // M11
    public async Task M11_EmptyList_IsRejectedAndKeepsTheMembership()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var result = await _sut.AssignTenantsAsync(target.Id, new AssignTenantsRequest(new List<Guid>()));

        Assert.True(result.IsFailure);
        Assert.Equal(EmptySelectionError, result.Error.Code);

        Context.ChangeTracker.Clear();
        Assert.Single(await MembershipsAsync(target.Id));
    }

    [Fact] // M12
    public async Task M12_EmptyList_OnMultiTenantTarget_KeepsBoth()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);

        var result = await _sut.AssignTenantsAsync(target.Id, new AssignTenantsRequest(new List<Guid>()));

        Assert.True(result.IsFailure);
        Assert.Equal(EmptySelectionError, result.Error.Code);

        Context.ChangeTracker.Clear();
        var memberships = await MembershipsAsync(target.Id);
        Assert.Equal(2, memberships.Count);
        Assert.Contains(memberships, ut => ut.TenantId == a.Id);
        Assert.Contains(memberships, ut => ut.TenantId == b.Id);
    }

    // ── M14 — duplicates ──────────────────────────────────────────────────────

    [Fact] // M14
    public async Task M14_RepeatedSaves_DoNotCreateDuplicateMembershipRows()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        Assert.True((await _sut.AssignTenantsAsync(target.Id, Save(a.Id))).IsSuccess);
        Assert.True((await _sut.AssignTenantsAsync(target.Id, Save(a.Id))).IsSuccess);
        Assert.True((await _sut.AssignTenantsAsync(target.Id, Save(a.Id, a.Id))).IsSuccess);

        Context.ChangeTracker.Clear();
        Assert.Single(await MembershipsAsync(target.Id));
    }

    // ── Fixture integrity ─────────────────────────────────────────────────────

    [Fact]
    public async Task Fixture_TenantBTargetGenuinelyBelongsToTenantB()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        Assert.True(await Users.IsAssignedToTenantAsync(target.Id, b.Id));
        Assert.False(await Users.IsAssignedToTenantAsync(target.Id, a.Id));
    }
}
