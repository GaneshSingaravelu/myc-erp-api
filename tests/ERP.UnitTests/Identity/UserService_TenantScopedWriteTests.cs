using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;

namespace ERP.UnitTests.Identity;

/// <summary>
/// CT-4 remediation, phase P3 — tenant scoping of PUT /users/{id}.
///
/// CT-4 confirmed at runtime that a Tenant A administrator could successfully PUT a
/// user outside Tenant A (F2). The write was a no-op by construction there, so what
/// was proven was the authorization decision, not data damage. These tests pin the
/// corrected decision and also assert that a rejected write leaves persisted state
/// untouched — the part CT-4 could not demonstrate.
///
/// <para>DELETE is deliberately absent. It deletes the global identity, so a simple
/// membership gate would authorise cross-tenant destruction; see
/// <c>UserService.DeleteAsync</c>. Blocked pending an owner decision.</para>
///
/// <para>Relational (SQLite) for the same reason as the P2 read tests, and every
/// negative case uses a target with a real active Tenant B membership so a pass
/// cannot come from an empty fixture.</para>
/// </summary>
public class UserService_TenantScopedWriteTests : RelationalIdentityTestBase
{
    private readonly UserService _sut;

    public UserService_TenantScopedWriteTests()
    {
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork, TenantProvider);
    }

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

    private static UpdateUserRequest Edit(string marker) =>
        new($"{marker}@edited.local", marker, "Edited", true);

    // ── W1 / W5 — permitted ───────────────────────────────────────────────────

    [Fact] // W1
    public async Task Update_TenantATarget_IsPermitted()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await MemberOfAsync("alice", a.Id);

        var result = await _sut.UpdateAsync(target.Id, Edit("alice"));

        Assert.True(result.IsSuccess);
        Assert.Equal("alice@edited.local", result.Value.Email);
        Assert.Equal("alice", result.Value.FirstName);
    }

    [Fact] // W5
    public async Task Update_MultiTenantTarget_IsPermittedBecauseActiveInTenantA()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);

        var result = await _sut.UpdateAsync(target.Id, Edit("dual"));

        Assert.True(result.IsSuccess);
        Assert.Equal("dual@edited.local", result.Value.Email);
    }

    [Fact] // W6
    public async Task Update_MultiTenantTarget_ResponseExposesOnlyTenantARoles()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var adminRole = await PersistAsync(CreateTestRole("Admin"));
        var viewerRole = await PersistAsync(CreateTestRole("Viewer"));
        await GrantAsync(target.Id, adminRole.Id, a.Id);
        await GrantAsync(target.Id, viewerRole.Id, b.Id);

        var result = await _sut.UpdateAsync(target.Id, Edit("dual"));

        // A successful write must not echo back what the caller may not read.
        Assert.Equal(new[] { "Admin" }, result.Value.Roles);
        Assert.DoesNotContain("Viewer", result.Value.Roles);
    }

    // ── W2 / W3 / W4 — denied ─────────────────────────────────────────────────

    [Fact] // W2 — the F2 regression test
    public async Task Update_GenuineTenantBTarget_IsNotFoundForTenantA()
    {
        var (_, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        var result = await _sut.UpdateAsync(target.Id, Edit("bob"));

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact] // W3
    public async Task Update_TenantLessTarget_IsNotFound()
    {
        await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("nomad"));

        var result = await _sut.UpdateAsync(target.Id, Edit("nomad"));

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact] // W4
    public async Task Update_InactiveTenantAMembership_IsNotFound()
    {
        var (a, _) = await TwoTenantsAsync();
        var target = await PersistAsync(CreateTestUser("lapsed"));
        await PersistAsync(CreateMembership(target.Id, a.Id, isActive: false));

        var result = await _sut.UpdateAsync(target.Id, Edit("lapsed"));

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    // ── W7 — a denied write must not have written ─────────────────────────────

    [Fact] // W7
    public async Task Update_DeniedWrite_LeavesEveryPersistedFieldUnchanged()
    {
        // CT-4 proved only that the request was authorised; it used identical values,
        // so it could not show whether a denial actually prevents persistence.
        var (_, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        var originalEmail = target.Email;
        var originalFirst = target.FirstName;
        var originalLast = target.LastName;
        var originalActive = target.IsActive;

        var result = await _sut.UpdateAsync(
            target.Id, new UpdateUserRequest("hacked@evil.local", "Hacked", "Hacked", false));

        Assert.True(result.IsFailure);

        Context.ChangeTracker.Clear();
        var reloaded = await Users.GetByIdAsync(target.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(originalEmail, reloaded!.Email);
        Assert.Equal(originalFirst, reloaded.FirstName);
        Assert.Equal(originalLast, reloaded.LastName);
        Assert.Equal(originalActive, reloaded.IsActive);
    }

    // ── W8 — a permitted write must not spill sideways ────────────────────────

    [Fact] // W8
    public async Task Update_TenantATarget_LeavesTenantBMembershipsAndRolesIntact()
    {
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("dual", a.Id, b.Id);
        var adminRole = await PersistAsync(CreateTestRole("Admin"));
        var viewerRole = await PersistAsync(CreateTestRole("Viewer"));
        await GrantAsync(target.Id, adminRole.Id, a.Id);
        await GrantAsync(target.Id, viewerRole.Id, b.Id);

        var result = await _sut.UpdateAsync(target.Id, Edit("dual"));
        Assert.True(result.IsSuccess);

        Context.ChangeTracker.Clear();

        // Tenant B's membership survives.
        Assert.True(await Users.IsAssignedToTenantAsync(target.Id, b.Id));

        // Tenant B's role assignment survives, unmodified.
        var bRoles = await Users.GetUserRoleAssignmentsInTenantAsync(target.Id, b.Id);
        var bRole = Assert.Single(bRoles);
        Assert.Equal(viewerRole.Id, bRole.RoleId);

        // And Tenant A's own assignment is untouched by the field update.
        var aRoles = await Users.GetUserRoleAssignmentsInTenantAsync(target.Id, a.Id);
        Assert.Single(aRoles);
    }

    // ── Fixture integrity ─────────────────────────────────────────────────────

    [Fact]
    public async Task Fixture_TenantBTargetGenuinelyBelongsToTenantB()
    {
        // Guards W2 and W7: without this, those could pass because the target was in
        // no tenant rather than because tenant scoping worked.
        var (a, b) = await TwoTenantsAsync();
        var target = await MemberOfAsync("bob", b.Id);

        Assert.True(await Users.IsAssignedToTenantAsync(target.Id, b.Id));
        Assert.False(await Users.IsAssignedToTenantAsync(target.Id, a.Id));
    }
}
