using ERP.Application.Abstractions.Identity.Contracts;
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
        _sut = new UserService(Users, Roles, Tenants, UnitOfWork);
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
        var role = await PersistAsync(CreateTestRole("Admin"));
        var user = CreateTestUser("alice");
        user.UserRoles.Add(new AppUserRole { RoleId = role.Id });
        await PersistAsync(user);

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
        await PersistAsync(CreateTestUser("charlie"));
        await PersistAsync(CreateTestUser("alice"));
        await PersistAsync(CreateTestUser("bob"));

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
        var user = await PersistAsync(CreateTestUser("alice"));
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

    /// <summary>Seeds a tenant and makes <paramref name="user"/> a member of it.</summary>
    private async Task<Tenant> SeedTenantWithMembershipAsync(AppUser user, string code = "ABC")
    {
        var tenant = await PersistAsync(CreateTestTenant(code));
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
        var role = await PersistAsync(CreateTestRole("Admin"));
        var request = new AssignRolesRequest(Guid.NewGuid(), new[] { role.Id });

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
        var user = await PersistAsync(CreateTestUser("alice"));
        var role = await PersistAsync(CreateTestRole("Admin"));
        var request = new AssignRolesRequest(Guid.NewGuid(), new[] { role.Id });

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
    public async Task GetUserTenants_ReturnsActiveTenantAssignments()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant1 = await PersistAsync(CreateTestTenant("ABC"));
        var tenant2 = await PersistAsync(CreateTestTenant("XYZ"));

        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = tenant1.Id,
            IsDefault = true, IsActive = true, JoinedAt = DateTime.UtcNow
        });
        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = tenant2.Id,
            IsDefault = false, IsActive = false, JoinedAt = DateTime.UtcNow // inactive
        });

        var result = await _sut.GetUserTenantsAsync(user.Id);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("ABC", result.Value[0].TenantCode);
        Assert.True(result.Value[0].IsDefault);
    }

    // ── AssignTenantsAsync ──

    [Fact]
    public async Task AssignTenants_WhenUserNotFound_ReturnsNotFound()
    {
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        var request = new AssignTenantsRequest(new[] { tenant.Id });

        var result = await _sut.AssignTenantsAsync(Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignTenants_WhenAnyTenantMissing_ReturnsNotFound()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("ABC"));
        var request = new AssignTenantsRequest(new[] { tenant.Id, Guid.NewGuid() });

        var result = await _sut.AssignTenantsAsync(user.Id, request);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignTenants_ReplacesExistingAssignments()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var oldTenant = await PersistAsync(CreateTestTenant("OLD"));
        var newTenant1 = await PersistAsync(CreateTestTenant("NEW1"));
        var newTenant2 = await PersistAsync(CreateTestTenant("NEW2"));

        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = oldTenant.Id,
            IsDefault = true, IsActive = true, JoinedAt = DateTime.UtcNow
        });

        var request = new AssignTenantsRequest(new[] { newTenant1.Id, newTenant2.Id });
        var result = await _sut.AssignTenantsAsync(user.Id, request);

        Assert.True(result.IsSuccess);
        var assigned = await Context.Set<UserTenant>()
            .Where(ut => ut.UserId == user.Id)
            .ToListAsync();

        Assert.Equal(2, assigned.Count);
        Assert.Contains(assigned, ut => ut.TenantId == newTenant1.Id && ut.IsDefault);
        Assert.Contains(assigned, ut => ut.TenantId == newTenant2.Id && !ut.IsDefault);
        Assert.DoesNotContain(assigned, ut => ut.TenantId == oldTenant.Id);
    }

    [Fact]
    public async Task AssignTenants_WithEmptyList_ClearsAllAssignments()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var tenant = await PersistAsync(CreateTestTenant("ABC"));

        await PersistAsync(new UserTenant
        {
            UserId = user.Id, TenantId = tenant.Id,
            IsDefault = true, IsActive = true, JoinedAt = DateTime.UtcNow
        });

        var request = new AssignTenantsRequest(new List<Guid>());
        var result = await _sut.AssignTenantsAsync(user.Id, request);

        Assert.True(result.IsSuccess);
        var remaining = await Context.Set<UserTenant>()
            .Where(ut => ut.UserId == user.Id)
            .ToListAsync();
        Assert.Empty(remaining);
    }
}
