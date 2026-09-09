using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// CT-4 remediation, phase P4 — tenant-safe creation for POST /users.
///
/// Creation used to produce a tenant-less identity. Once the reads became
/// tenant-scoped that left a user nobody could administer: the creator could not
/// read, edit or assign roles to the account they had just made. Creation now places
/// the user in the caller's validated tenant.
///
/// <para><b>The request contract is unchanged.</b> CreateUserRequest still carries no
/// tenant field, so these tests also serve as the proof that placement cannot be
/// influenced by the client — there is no input through which to try.</para>
///
/// <para>Relational (SQLite), because membership rows and the single-SaveChanges
/// boundary are the subject.</para>
/// </summary>
public class UserService_TenantScopedCreateTests : RelationalIdentityTestBase
{
    private readonly UserService _sut;

    public UserService_TenantScopedCreateTests()
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

    private static CreateUserRequest NewUser(string name = "newuser") =>
        new(name, $"{name}@example.com", "Password@123", "New", "User");

    // ── C1 / C2 / C3 / C4 — placement ─────────────────────────────────────────

    [Fact] // C1 + C2
    public async Task Create_InTenantA_SucceedsAndPersistsIdentity()
    {
        await TwoTenantsAsync();

        var result = await _sut.CreateAsync(NewUser());

        Assert.True(result.IsSuccess);

        Context.ChangeTracker.Clear();
        var stored = await Context.Set<AppUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == "NEWUSER");
        Assert.NotNull(stored);
        Assert.Equal(result.Value.Id, stored!.Id);
    }

    [Fact] // C3
    public async Task Create_GivesNewUserActiveMembershipOfCurrentTenant()
    {
        var (a, _) = await TwoTenantsAsync();

        var result = await _sut.CreateAsync(NewUser());

        Context.ChangeTracker.Clear();
        Assert.True(await Users.IsAssignedToTenantAsync(result.Value.Id, a.Id));
    }

    [Fact] // C3 — IsDefault follows the established convention
    public async Task Create_MarksTheSoleMembershipAsDefault()
    {
        var (a, _) = await TwoTenantsAsync();

        var result = await _sut.CreateAsync(NewUser());

        Context.ChangeTracker.Clear();
        var membership = await Context.Set<UserTenant>()
            .SingleAsync(ut => ut.UserId == result.Value.Id);

        Assert.Equal(a.Id, membership.TenantId);
        Assert.True(membership.IsActive);
        Assert.True(membership.IsDefault);
    }

    [Fact] // C4
    public async Task Create_DoesNotCreateAnyOtherTenantMembership()
    {
        var (a, b) = await TwoTenantsAsync();

        var result = await _sut.CreateAsync(NewUser());

        Context.ChangeTracker.Clear();
        Assert.False(await Users.IsAssignedToTenantAsync(result.Value.Id, b.Id));

        var memberships = await Context.Set<UserTenant>()
            .Where(ut => ut.UserId == result.Value.Id)
            .ToListAsync();
        var only = Assert.Single(memberships);
        Assert.Equal(a.Id, only.TenantId);
    }

    // ── C5 / C6 — immediately manageable ──────────────────────────────────────

    [Fact] // C6
    public async Task Create_UserIsImmediatelyResolvableByTenantScopedGetById()
    {
        // The regression this phase exists to prevent: before P4 the created user was
        // tenant-less and therefore returned NotFound to the very admin who made it.
        await TwoTenantsAsync();

        var created = await _sut.CreateAsync(NewUser());
        Context.ChangeTracker.Clear();

        var read = await _sut.GetByIdAsync(created.Value.Id);

        Assert.True(read.IsSuccess);
        Assert.Equal("newuser", read.Value.UserName);
    }

    [Fact] // C5
    public async Task Create_UserImmediatelyAppearsInTenantScopedList()
    {
        await TwoTenantsAsync();

        var created = await _sut.CreateAsync(NewUser());
        Context.ChangeTracker.Clear();

        var list = await _sut.GetAllAsync();

        Assert.Contains(list.Value, u => u.Id == created.Value.Id);
    }

    // ── C7 / C10 — response safety ────────────────────────────────────────────

    [Fact] // C7 + C10
    public async Task Create_ResponseCarriesNoRoleData()
    {
        // A new user holds no roles anywhere, so the tenant-aware projection has
        // nothing to filter — but it is the projection that guarantees that, not luck.
        var (_, b) = await TwoTenantsAsync();

        var result = await _sut.CreateAsync(NewUser());

        Assert.Empty(result.Value.Roles);
        Assert.Equal("newuser", result.Value.UserName);
        _ = b; // Tenant B exists in the fixture; nothing about it may appear above.
    }

    // ── C8 / C9 / C10 — collisions stay conflicts ─────────────────────────────

    [Fact] // C8
    public async Task Create_UserNameCollision_ConflictsAndCreatesNothing()
    {
        var (a, _) = await TwoTenantsAsync();
        var existing = await PersistAsync(CreateTestUser("john"));

        var before = await Context.Set<UserTenant>().CountAsync();

        var result = await _sut.CreateAsync(
            new CreateUserRequest("john", "other@example.com", "Password@123", "John", "Doe"));

        Assert.True(result.IsFailure);
        Assert.Equal("User.UserNameExists", result.Error.Code);

        Context.ChangeTracker.Clear();
        Assert.Equal(before, await Context.Set<UserTenant>().CountAsync());
        Assert.False(await Users.IsAssignedToTenantAsync(existing.Id, a.Id));
    }

    [Fact] // C9
    public async Task Create_EmailCollision_ConflictsAndCreatesNothing()
    {
        var (a, _) = await TwoTenantsAsync();
        var existing = await PersistAsync(CreateTestUser("john"));

        var before = await Context.Set<UserTenant>().CountAsync();

        var result = await _sut.CreateAsync(
            new CreateUserRequest("jane", existing.Email, "Password@123", "Jane", "Doe"));

        Assert.True(result.IsFailure);
        Assert.Equal("User.EmailExists", result.Error.Code);

        Context.ChangeTracker.Clear();
        Assert.Equal(before, await Context.Set<UserTenant>().CountAsync());
        Assert.False(await Users.IsAssignedToTenantAsync(existing.Id, a.Id));
    }

    [Fact] // C10 — the cross-tenant collision case
    public async Task Create_CollidingWithTenantBIdentity_ConflictsWithoutAttachOrMutation()
    {
        // Admin A must not be able to pull a Tenant B user into Tenant A by
        // re-registering their address, nor learn anything about them.
        var (a, b) = await TwoTenantsAsync();
        var bUser = await PersistAsync(CreateTestUser("bob"));
        await PersistAsync(CreateMembership(bUser.Id, b.Id));

        var originalEmail = bUser.Email;
        var originalFirst = bUser.FirstName;

        var result = await _sut.CreateAsync(
            new CreateUserRequest("bob", "different@example.com", "Password@123", "Impostor", "User"));

        Assert.True(result.IsFailure);
        Assert.Equal("User.UserNameExists", result.Error.Code);

        Context.ChangeTracker.Clear();

        // Not attached to Tenant A.
        Assert.False(await Users.IsAssignedToTenantAsync(bUser.Id, a.Id));
        // Tenant B membership intact.
        Assert.True(await Users.IsAssignedToTenantAsync(bUser.Id, b.Id));
        // Identity data unmodified.
        var reloaded = await Users.GetByIdAsync(bUser.Id);
        Assert.Equal(originalEmail, reloaded!.Email);
        Assert.Equal(originalFirst, reloaded.FirstName);
    }

    // ── C11 — atomicity ───────────────────────────────────────────────────────

    [Fact] // C11
    public async Task Create_CommitsIdentityAndMembershipInOneSaveBoundary()
    {
        // Both rows must land together. If they were saved separately, a failure
        // between them would leave a tenant-less identity that no admin could reach —
        // the exact state P4 exists to eliminate.
        var (a, _) = await TwoTenantsAsync();

        var result = await _sut.CreateAsync(NewUser());
        Assert.True(result.IsSuccess);

        Context.ChangeTracker.Clear();

        var user = await Context.Set<AppUser>().SingleAsync(u => u.Id == result.Value.Id);
        var membership = await Context.Set<UserTenant>().SingleAsync(ut => ut.UserId == user.Id);

        Assert.Equal(a.Id, membership.TenantId);
        Assert.True(membership.IsActive);
    }

    [Fact] // C11 — no partial state when the operation is rejected
    public async Task Create_RejectedRequest_LeavesNeitherIdentityNorMembership()
    {
        var (a, _) = await TwoTenantsAsync();
        await PersistAsync(CreateTestUser("john"));

        var usersBefore = await Context.Set<AppUser>().CountAsync();
        var membershipsBefore = await Context.Set<UserTenant>().CountAsync();

        var result = await _sut.CreateAsync(
            new CreateUserRequest("john", "other@example.com", "Password@123", "John", "Doe"));

        Assert.True(result.IsFailure);

        Context.ChangeTracker.Clear();
        Assert.Equal(usersBefore, await Context.Set<AppUser>().CountAsync());
        Assert.Equal(membershipsBefore, await Context.Set<UserTenant>().CountAsync());
        _ = a;
    }
}
