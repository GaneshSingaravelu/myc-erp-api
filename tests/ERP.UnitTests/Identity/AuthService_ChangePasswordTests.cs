using ERP.Identity.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for AuthService.ChangePasswordAsync — covers password validation,
/// history enforcement, SecurityStamp rotation, session revocation, and JTI cache invalidation.
/// </summary>
public class AuthService_ChangePasswordTests : AuthServiceTestBase
{
    [Fact]
    public async Task ChangePassword_WhenUserNotFound_ReturnsNotFound()
    {
        var result = await Sut.ChangePasswordAsync(Guid.NewGuid(), "old", "new");

        Assert.True(result.IsFailure);
        Assert.Equal("User.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task ChangePassword_WhenCurrentPasswordWrong_ReturnsInvalidPassword()
    {
        var user = CreateTestUser(password: "Correct@123");
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var result = await Sut.ChangePasswordAsync(user.Id, "WrongPassword!", "NewPassword@123");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidPassword", result.Error.Code);
    }

    [Fact]
    public async Task ChangePassword_WhenNewPasswordSameAsCurrent_ReturnsPasswordReused()
    {
        var user = CreateTestUser(password: "Current@123");
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var result = await Sut.ChangePasswordAsync(user.Id, "Current@123", "Current@123");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.PasswordReused", result.Error.Code);
        Assert.Contains("same as the current", result.Error.Message);
    }

    [Fact]
    public async Task ChangePassword_WhenNewPasswordInHistory_ReturnsPasswordReused()
    {
        var user = CreateTestUser(password: "Current@123");
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        // Seed password history with an old password
        var oldPasswordHash = BCrypt.Net.BCrypt.HashPassword("OldPassword@1", workFactor: 4);
        Context.Set<UserPasswordHistory>().Add(new UserPasswordHistory
        {
            UserId = user.Id,
            PasswordHash = oldPasswordHash,
            ChangedAt = DateTime.UtcNow.AddDays(-30)
        });
        await Context.SaveChangesAsync();

        // Try to reuse the old password
        var result = await Sut.ChangePasswordAsync(user.Id, "Current@123", "OldPassword@1");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.PasswordReused", result.Error.Code);
        Assert.Contains("last 5", result.Error.Message);
    }

    [Fact]
    public async Task ChangePassword_WhenNewPasswordNotInHistory_Succeeds()
    {
        var user = CreateTestUser(password: "Current@123");
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var result = await Sut.ChangePasswordAsync(user.Id, "Current@123", "BrandNew@456");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ChangePassword_OnSuccess_StoresOldHashInHistory()
    {
        var user = CreateTestUser(password: "Current@123");
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var oldHash = user.PasswordHash;

        await Sut.ChangePasswordAsync(user.Id, "Current@123", "BrandNew@456");

        var historyEntry = await Context.Set<UserPasswordHistory>()
            .FirstOrDefaultAsync(h => h.UserId == user.Id);
        Assert.NotNull(historyEntry);
        Assert.Equal(oldHash, historyEntry.PasswordHash);
    }

    [Fact]
    public async Task ChangePassword_OnSuccess_RotatesSecurityStamp()
    {
        var user = CreateTestUser(password: "Current@123");
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        var oldStamp = user.SecurityStamp;

        await Sut.ChangePasswordAsync(user.Id, "Current@123", "BrandNew@456");

        var updatedUser = await Context.Set<AppUser>().FindAsync(user.Id);
        Assert.NotEqual(oldStamp, updatedUser!.SecurityStamp);
    }

    [Fact]
    public async Task ChangePassword_OnSuccess_UpdatesPasswordHash()
    {
        var user = CreateTestUser(password: "Current@123");
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        await Sut.ChangePasswordAsync(user.Id, "Current@123", "BrandNew@456");

        var updatedUser = await Context.Set<AppUser>().FindAsync(user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify("BrandNew@456", updatedUser!.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("Current@123", updatedUser.PasswordHash));
    }

    [Fact]
    public async Task ChangePassword_OnSuccess_SetsMustChangePasswordToFalse()
    {
        var user = CreateTestUser(password: "Current@123");
        user.MustChangePassword = true;
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        await Sut.ChangePasswordAsync(user.Id, "Current@123", "BrandNew@456");

        var updatedUser = await Context.Set<AppUser>().FindAsync(user.Id);
        Assert.False(updatedUser!.MustChangePassword);
    }

    [Fact]
    public async Task ChangePassword_OnSuccess_RevokesAllActiveSessionsAndInvalidatesJtis()
    {
        var user = CreateTestUser(password: "Current@123");
        var tenant = CreateTestTenant();
        await Context.Set<AppUser>().AddAsync(user);
        await Context.Set<Tenant>().AddAsync(tenant);
        await Context.SaveChangesAsync();

        // Create 3 active sessions (multi-device)
        var session1 = CreateTestSession(user.Id, tenant.Id, "refresh-1", jti: "jti-1");
        var session2 = CreateTestSession(user.Id, tenant.Id, "refresh-2", jti: "jti-2");
        var session3 = CreateTestSession(user.Id, tenant.Id, "refresh-3", jti: "jti-3");
        await Context.Set<UserSession>().AddRangeAsync(session1, session2, session3);
        await Context.SaveChangesAsync();

        await Sut.ChangePasswordAsync(user.Id, "Current@123", "BrandNew@456");

        // All 3 sessions must be revoked
        var sessions = await Context.Set<UserSession>()
            .Where(s => s.UserId == user.Id)
            .ToListAsync();
        Assert.All(sessions, s =>
        {
            Assert.True(s.IsRevoked);
            Assert.Equal("PasswordChanged", s.RevokedReason);
        });

        // All 3 JTI cache entries must be invalidated
        CacheMock.Verify(c => c.Remove("jti_valid:jti-1"), Times.Once);
        CacheMock.Verify(c => c.Remove("jti_valid:jti-2"), Times.Once);
        CacheMock.Verify(c => c.Remove("jti_valid:jti-3"), Times.Once);
    }

    [Fact]
    public async Task ChangePassword_WhenNoActiveSessions_StillSucceeds()
    {
        var user = CreateTestUser(password: "Current@123");
        await Context.Set<AppUser>().AddAsync(user);
        await Context.SaveChangesAsync();

        // No sessions exist
        var result = await Sut.ChangePasswordAsync(user.Id, "Current@123", "BrandNew@456");

        Assert.True(result.IsSuccess);
        CacheMock.Verify(c => c.Remove(It.IsAny<string>()), Times.Never);
    }
}
