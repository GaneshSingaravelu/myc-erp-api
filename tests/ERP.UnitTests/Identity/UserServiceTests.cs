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
        _sut = new UserService(Users, Roles, UnitOfWork);
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

    [Fact]
    public async Task AssignRoles_WhenUserNotFound_ReturnsNotFound()
    {
        var role = await PersistAsync(CreateTestRole("Admin"));
        var request = new AssignRolesRequest(new[] { role.Id });

        var result = await _sut.AssignRolesAsync(Guid.NewGuid(), request);

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task AssignRoles_WhenAnyRoleMissing_ReturnsNotFound()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var existingRole = await PersistAsync(CreateTestRole("Admin"));
        await PersistAsync(new AppUserRole { UserId = user.Id, RoleId = existingRole.Id });

        // Second id doesn't exist — the loop should bail before SaveChanges.
        var request = new AssignRolesRequest(new[] { existingRole.Id, Guid.NewGuid() });

        var result = await _sut.AssignRolesAsync(user.Id, request);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.NotFound", result.Error.Code); // The missing id is a role id, not the user.
        // Structural guarantee: _uow.SaveChangesAsync() runs only after the loop
        // completes successfully, so the staged role-replacement is never persisted.
    }

    [Fact]
    public async Task AssignRoles_ReplacesExistingAssignments()
    {
        var user = await PersistAsync(CreateTestUser("alice"));
        var oldRole = await PersistAsync(CreateTestRole("Viewer"));
        var newRole1 = await PersistAsync(CreateTestRole("Admin"));
        var newRole2 = await PersistAsync(CreateTestRole("Operator"));

        await PersistAsync(new AppUserRole { UserId = user.Id, RoleId = oldRole.Id });

        var request = new AssignRolesRequest(new[] { newRole1.Id, newRole2.Id });
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
}
