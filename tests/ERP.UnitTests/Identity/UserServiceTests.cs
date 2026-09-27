using ERP.Application.Abstractions.Identity.Contracts;
using Moq;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Entities;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for UserService. Verifies the repository-based refactor preserves
/// the original business rules: duplicate-checks on username/email, soft-delete
/// semantics, and role-assignment replacement.
/// </summary>
public class UserServiceTests : IdentityServiceTestBase
{
    private readonly UserService _sut;

    public UserServiceTests()
    {
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork, TenantProvider);
    }

    // ── GetByIdAsync ──

    [Fact]
    public async Task GetById_WhenUserNotFound_ReturnsNotFound()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task GetById_WhenUserExists_ReturnsUserWithRoleNames()
    {
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        var user = CreateTestUser("alice");
        user.UserRoles.Add(new AppUserRole { RoleId = role.Id, TenantId = tenant.Id });
        await PersistAsync(user);
        await PersistAsync(CreateMembership(user.Id, tenant.Id));
        SetCurrentTenant(tenant.Id);

        var result = await _sut.GetByIdAsync(user.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("alice", result.Value.UserName);
        Assert.Single(result.Value.Roles);
        Assert.Equal("Admin", result.Value.Roles[0]);
    }

    // ── GetAllAsync ──

    [Fact]
    public async Task GetAll_ReturnsUsersOrderedByUserName()
    {
        // Every user must be an active member of the acting tenant to appear at all —
        // the list is a tenant roster now, not a workspace-wide identity dump.
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        SetCurrentTenant(tenant.Id);

        foreach (var name in new[] { "charlie", "alice", "bob" })
        {
            var u = await PersistAsync(CreateTestUser(name));
            await PersistAsync(CreateMembership(u.Id, tenant.Id));
        }

        var result = await _sut.GetAllAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.Count);
        Assert.Equal(new[] { "alice", "bob", "charlie" }, result.Value.Select(u => u.UserName));
    }

    // ── CreateAsync ──

    [Fact]
    public async Task Create_WhenUserNameExists_ReturnsConflict()
    {
        await PersistAsync(CreateTestUser("john"));

        var request = new CreateUserRequest("john", "other@example.com", "Password@123", "John", "Doe");
        var result = await _sut.CreateAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("User.UserNameExists", result.Error.Code);
    }

    [Fact]
    public async Task Create_WhenEmailExists_ReturnsConflict()
    {
        var existing = CreateTestUser("john");
        await PersistAsync(existing);

        var request = new CreateUserRequest("jane", existing.Email, "Password@123", "Jane", "Doe");
        var result = await _sut.CreateAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("User.EmailExists", result.Error.Code);
    }

    [Fact]
    public async Task Create_WithValidData_PersistsUserWithHashedPassword()
    {
        // Creation now also writes a UserTenant for the acting tenant, so the tenant
        // must be a real one. Without this the membership would point at Guid.Empty and
        // pass only because the InMemory provider does not enforce foreign keys —
        // the failure mode RelationalIdentityTestBase exists to rule out. The password
        // and normalisation assertions below are unchanged.
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        SetCurrentTenant(tenant.Id);

        var request = new CreateUserRequest("newuser", "new@example.com", "Password@123", "New", "User");

        var result = await _sut.CreateAsync(request);

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppUser>().FirstAsync(u => u.NormalizedUserName == "NEWUSER");
        Assert.NotEqual("Password@123", stored.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("Password@123", stored.PasswordHash));
        Assert.Equal("NEW@EXAMPLE.COM", stored.NormalizedEmail);
    }

    // ── UpdateAsync ──

    [Fact]
    public async Task Update_WhenUserNotFound_ReturnsNotFound()
    {
        var request = new UpdateUserRequest("x@example.com", "X", "Y", true);

        var result = await _sut.UpdateAsync(Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task Update_PersistsNewFieldsAndDeactivation()
    {
        // Update is tenant-scoped since P3: the target must be an active member of
        // the acting tenant. The assertions below — that the new field values are
        // persisted, including deactivation — are unchanged; only the setup now
        // reflects that a user is reached through a tenant rather than globally.
        var tenant = await PersistAsync(CreateTestTenant("AAA"));
        SetCurrentTenant(tenant.Id);
        var user = await PersistAsync(CreateTestUser("alice"));
        await PersistAsync(CreateMembership(user.Id, tenant.Id));
        var request = new UpdateUserRequest("alice2@example.com", "Alice", "Smith", IsActive: false);

        var result = await _sut.UpdateAsync(user.Id, request);

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<AppUser>().FirstAsync(u => u.Id == user.Id);
        Assert.Equal("alice2@example.com", stored.Email);
        Assert.Equal("ALICE2@EXAMPLE.COM", stored.NormalizedEmail);
        Assert.False(stored.IsActive);
    }

    // ── DeleteAsync ──

    [Fact]
    public async Task Delete_WhenUserExists_SoftDeletesViaInterceptor()
    {
        var user = await PersistAsync(CreateTestUser("alice"));

        var result = await _sut.DeleteAsync(user.Id);

        Assert.True(result.IsSuccess);
        // Soft-delete query filter should hide the user from default queries.
        var visible = await Context.Set<AppUser>().FirstOrDefaultAsync(u => u.Id == user.Id);
        Assert.Null(visible);
        // But the row still exists with IsDeleted = true.
        var raw = await Context.Set<AppUser>().IgnoreQueryFilters().FirstAsync(u => u.Id == user.Id);
        Assert.True(raw.IsDeleted);
    }

    // ── AssignRolesAsync ──
    //
    // Role assignment is tenant-scoped: UserRoles is keyed on
    // (UserId, RoleId, TenantId). These cover the validation ladder, which needs
    // no relational provider. The integrity guarantees — the persisted TenantId,
    // cross-tenant preservation and the unique index — live in
    // UserService_AssignRolesRelationalTests, because the InMemory provider
    // enforces neither foreign keys nor unique indexes and would pass regardless.

    /// <summary>
    /// Seeds a tenant, makes <paramref name="user"/> a member of it, and acts inside it.
    ///
    /// The acting context is set here because role assignment now checks actor
    /// authority before anything else: a request naming a tenant the caller is not
    /// authenticated for is refused outright.
    /// </summary>
    private async Task<Tenant> SeedTenantWithMembershipAsync(AppUser user, string code = "ABC")
    {
        var tenant = await PersistAsync(CreateTestTenant(code));
        SetCurrentTenant(tenant.Id, code);
        await PersistAsync(new UserTenant
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            IsDefault = true,
            IsActive = true,
            JoinedAt = DateTime.UtcNow
        });
        return tenant;
    }

    [Fact]
    public async Task AssignRoles_WhenUserNotFound_ReturnsNotFound()
    {
        // The actor gate runs first, so the request must name the tenant the caller is
        // authenticated for; otherwise this would fail as unauthorized and never reach
        // the user lookup it is meant to exercise.
        var role = await PersistAsync(CreateTestRole("Admin"));
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        SetCurrentTenant(tenant.Id);
        var request = new AssignRolesRequest(tenant.Id, new[] { role.Id });

        var result = await _sut.AssignRolesAsync(Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignRoles_WhenTenantIdIsEmpty_ReturnsValidationFailure()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var role = await PersistAsync(CreateTestRole("Admin"));

        // What a pre-tenant caller sends: the omitted tenantId binds to Guid.Empty.
        // It must be rejected here rather than reaching the database as an FK error.
        var request = new AssignRolesRequest(Guid.Empty, new[] { role.Id });

        var result = await _sut.AssignRolesAsync(user.Id, request);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.TenantRequired", result.Error.Code);
    }

    [Fact]
    public async Task AssignRoles_WhenTenantNotFound_ReturnsNotFound()
    {
        // Reaching this error now requires the acting context itself to name a tenant
        // that does not exist — i.e. a misconfigured tenant provider. That is exactly
        // the case the check still guards: a broken provider must fail closed rather
        // than become an authorization bypass. A body naming some OTHER absent tenant
        // is refused earlier, by the actor gate, and is covered in the P5 suite.
        var user = await PersistAsync(CreateTestUser("alice"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        var missingTenantId = Guid.NewGuid();
        SetCurrentTenant(missingTenantId);
        var request = new AssignRolesRequest(missingTenantId, new[] { role.Id });

        var result = await _sut.AssignRolesAsync(user.Id, request);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignRoles_WhenUserNotAssignedToTenant_ReturnsValidationFailure()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        var tenant = await PersistAsync(CreateTestTenant("ABC")); // no membership row
        SetCurrentTenant(tenant.Id);

        var request = new AssignRolesRequest(tenant.Id, new[] { role.Id });

        var result = await _sut.AssignRolesAsync(user.Id, request);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotInTenant", result.Error.Code);
    }

    [Fact]
    public async Task AssignRoles_WhenUserHasNoTenants_ReturnsNotInTenant()
    {
        // A user can legitimately have zero tenant memberships. They cannot receive
        // a tenant-scoped role until a tenant is assigned to them first.
        var user = await PersistAsync(CreateTestUser("orphan"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        SetCurrentTenant(tenant.Id);

        var result = await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenant.Id, new[] { role.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotInTenant", result.Error.Code);
        Assert.Empty(Context.Set<AppUserRole>().Where(ur => ur.UserId == user.Id));
    }

    [Fact]
    public async Task AssignRoles_WhenMembershipIsInactive_ReturnsNotInTenant()
    {
        // IsAssignedToTenantAsync filters on IsActive, so a deactivated membership
        // is not a membership — the same rule the login path applies.
        var user = await PersistAsync(CreateTestUser("alice"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        await PersistAsync(new UserTenant
        {
            UserId = user.Id,
            TenantId = tenant.Id,
            IsDefault = true,
            IsActive = false,
            JoinedAt = DateTime.UtcNow
        });
        SetCurrentTenant(tenant.Id);

        var result = await _sut.AssignRolesAsync(
            user.Id, new AssignRolesRequest(tenant.Id, new[] { role.Id }));

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotInTenant", result.Error.Code);
    }

    [Fact]
    public async Task AssignRoles_WhenAnyRoleMissing_ReturnsNotFoundAndPersistsNothing()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await SeedTenantWithMembershipAsync(user);
        var existingRole = await PersistAsync(CreateTestRole("Admin"));
        await PersistAsync(new AppUserRole
        {
            UserId = user.Id,
            RoleId = existingRole.Id,
            TenantId = tenant.Id
        });

        // Second id doesn't exist — validation must bail before anything is removed.
        var request = new AssignRolesRequest(tenant.Id, new[] { existingRole.Id, Guid.NewGuid() });

        var result = await _sut.AssignRolesAsync(user.Id, request);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code); // The missing id is a role id, not the user.

        // Stronger than the old structural argument: the pre-existing assignment is
        // still there, because removal now happens only after validation passes.
        var stillAssigned = await Context.Set<AppUserRole>()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        Assert.Equal(new[] { existingRole.Id }, stillAssigned);
    }

    [Fact]
    public async Task AssignRoles_ReplacesExistingAssignments()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await SeedTenantWithMembershipAsync(user);
        var oldRole = await PersistAsync(CreateTestRole("Viewer"));
        var newRole1 = await PersistAsync(CreateTestRole("Admin"));
        var newRole2 = await PersistAsync(CreateTestRole("Operator"));

        await PersistAsync(new AppUserRole
        {
            UserId = user.Id,
            RoleId = oldRole.Id,
            TenantId = tenant.Id
        });

        var request = new AssignRolesRequest(tenant.Id, new[] { newRole1.Id, newRole2.Id });
        var result = await _sut.AssignRolesAsync(user.Id, request);

        Assert.True(result.IsSuccess);
        var assigned = await Context.Set<AppUserRole>()
            .Where(ur => ur.UserId == user.Id)
            .Select(ur => ur.RoleId)
            .ToListAsync();

        Assert.Equal(2, assigned.Count);
        Assert.Contains(newRole1.Id, assigned);
        Assert.Contains(newRole2.Id, assigned);
        Assert.DoesNotContain(oldRole.Id, assigned);
    }

    // ── GetUserTenantsAsync ──

    [Fact]
    public async Task GetUserTenants_WhenUserNotFound_ReturnsNotFound()
    {
        var result = await _sut.GetUserTenantsAsync(Guid.NewGuid());

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task GetUserTenants_ReturnsOnlyActingTenantMembership()
    {
        // Was: "returns every active membership". A Tenant A administrator must not
        // discover from this endpoint that the user also belongs to XYZ, so the
        // response is now the acting tenant's membership alone.
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant1 = await PersistAsync(CreateTestTenant("ABC"));
        var tenant2 = await PersistAsync(CreateTestTenant("XYZ"));

        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = tenant1.Id,
            IsDefault = true, IsActive = true, JoinedAt = DateTime.UtcNow
        });
        // Active membership of a SECOND tenant — previously returned, now hidden.
        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = tenant2.Id,
            IsDefault = false, IsActive = true, JoinedAt = DateTime.UtcNow
        });

        SetCurrentTenant(tenant1.Id);

        var result = await _sut.GetUserTenantsAsync(user.Id);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("abc", result.Value[0].TenantCode);
        Assert.True(result.Value[0].IsDefault);

        // The security assertion: XYZ's existence is not disclosed to ABC's admin.
        Assert.DoesNotContain(result.Value, t => t.TenantCode == "xyz");
    }

    // ── AssignTenantsAsync ──
    //
    // The global replace-all contract these tests were written against has been
    // RETIRED BY OWNER SECURITY DECISION, not corrected as a setup mistake. It let a
    // Tenant A administrator delete another tenant's membership by submitting the only
    // membership the tenant-scoped read had shown them. Membership management is now
    // confined to the caller's own tenant, and neither attachment nor ejection is
    // supported here.

    [Fact]
    public async Task AssignTenants_WhenUserNotFound_ReturnsNotFound()
    {
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        SetCurrentTenant(tenant.Id, "ABC");
        var request = new AssignTenantsRequest(new[] { tenant.Id });

        var result = await _sut.AssignTenantsAsync(Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignTenants_WithATenantIdOtherThanCurrent_IsRefusedWithoutRevealingWhetherItExists()
    {
        // Superseded contract: this used to answer "Tenant.NotFound", which told the
        // caller whether an id they were not entitled to name was real. Any id other
        // than the current tenant is now simply unauthorized, existent or not.
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        await PersistAsync(CreateMembership(user.Id, tenant.Id));
        SetCurrentTenant(tenant.Id, "ABC");

        var request = new AssignTenantsRequest(new[] { tenant.Id, Guid.NewGuid() });

        var result = await _sut.AssignTenantsAsync(user.Id, request);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotAuthorized", result.Error.Code);
    }

    [Fact]
    public async Task AssignTenants_CurrentTenantSave_PreservesOtherTenantAssignments()
    {
        // Retired contract: submitting a list used to replace the user's ENTIRE
        // membership set, so naming only the current tenant deleted every other one.
        // The inverse is now asserted - the other tenant must survive untouched.
        var user = await PersistAsync(CreateTestUser("alice"));
        var current = await PersistAsync(CreateTestTenant("AAA"));
        var other = await PersistAsync(CreateTestTenant("BBB"));

        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = current.Id,
            IsDefault = false, IsActive = true, JoinedAt = DateTime.UtcNow
        });
        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = other.Id,
            IsDefault = true, IsActive = true, JoinedAt = DateTime.UtcNow
        });

        SetCurrentTenant(current.Id, "AAA");

        var result = await _sut.AssignTenantsAsync(
            user.Id, new AssignTenantsRequest(new[] { current.Id }));

        Assert.True(result.IsSuccess);
        var assigned = await Context.Set<UserTenant>()
            .Where(ut => ut.UserId == user.Id)
            .ToListAsync();

        Assert.Equal(2, assigned.Count);
        Assert.Contains(assigned, ut => ut.TenantId == current.Id);
        // The old implementation deleted this row. It must still be here, and still be
        // the default - the current tenant must not have been promoted.
        Assert.Contains(assigned, ut => ut.TenantId == other.Id && ut.IsDefault);
    }

    [Fact]
    public async Task AssignTenants_WithEmptyList_ReturnsValidationError_AndChangesNothing()
    {
        // Retired contract: an empty list used to clear every membership, leaving a
        // tenant-less identity that no tenant administrator could then see or manage.
        // Ejection is a separate lifecycle decision, so the request is now refused.
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("ABC"));

        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = tenant.Id,
            IsDefault = true, IsActive = true, JoinedAt = DateTime.UtcNow
        });
        SetCurrentTenant(tenant.Id, "ABC");

        var result = await _sut.AssignTenantsAsync(
            user.Id, new AssignTenantsRequest(new List<Guid>()));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.SelectionRequired", result.Error.Code);

        var remaining = await Context.Set<UserTenant>()
            .Where(ut => ut.UserId == user.Id)
            .ToListAsync();
        Assert.Single(remaining);
        Assert.True(remaining[0].IsDefault);
    }
}
