using ERP.Identity.DTOs;
using ERP.Identity.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for AuthService.RegisterAsync — covers duplicate checks,
/// user creation, and password hashing.
/// </summary>
public class AuthService_RegisterTests : AuthServiceTestBase
{
    [Fact]
    public async Task Register_WhenUserNameExists_ReturnsConflict()
    {
        var existingUser = CreateTestUser("john");
        await Context.Set<AppUser>().AddAsync(existingUser);
        await Context.SaveChangesAsync();

        var request = new RegisterRequest("john", "other@example.com", "Password@123", "John", "Doe");

        var result = await Sut.RegisterAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.UserNameExists", result.Error.Code);
    }

    [Fact]
    public async Task Register_WhenEmailExists_ReturnsConflict()
    {
        var existingUser = CreateTestUser("john");
        await Context.Set<AppUser>().AddAsync(existingUser);
        await Context.SaveChangesAsync();

        // Different username, same email
        var request = new RegisterRequest("jane", existingUser.Email, "Password@123", "Jane", "Doe");

        var result = await Sut.RegisterAsync(request);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.EmailExists", result.Error.Code);
    }

    [Fact]
    public async Task Register_WithValidData_CreatesUser()
    {
        var request = new RegisterRequest("newuser", "new@example.com", "Password@123", "New", "User");

        var result = await Sut.RegisterAsync(request);

        Assert.True(result.IsSuccess);
        Assert.Equal("newuser", result.Value.UserName);
        Assert.Equal("new@example.com", result.Value.Email);
        Assert.Equal("New", result.Value.FirstName);
        Assert.Equal("User", result.Value.LastName);
        Assert.True(result.Value.IsActive);
        Assert.Empty(result.Value.Roles);

        // Verify user persisted in DB
        var user = await Context.Set<AppUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == "NEWUSER");
        Assert.NotNull(user);
    }

    [Fact]
    public async Task Register_WithValidData_HashesPassword()
    {
        var request = new RegisterRequest("newuser", "new@example.com", "Password@123", "New", "User");

        await Sut.RegisterAsync(request);

        var user = await Context.Set<AppUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == "NEWUSER");
        Assert.NotNull(user);
        // Password must be BCrypt-hashed, not stored as plain text
        Assert.NotEqual("Password@123", user.PasswordHash);
        Assert.True(BCrypt.Net.BCrypt.Verify("Password@123", user.PasswordHash));
    }

    [Fact]
    public async Task Register_SetsNormalizedFields()
    {
        var request = new RegisterRequest("JohnDoe", "John@Example.COM", "Password@123", "John", "Doe");

        await Sut.RegisterAsync(request);

        var user = await Context.Set<AppUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == "JOHNDOE");
        Assert.NotNull(user);
        Assert.Equal("JOHNDOE", user.NormalizedUserName);
        Assert.Equal("JOHN@EXAMPLE.COM", user.NormalizedEmail);
    }
}
