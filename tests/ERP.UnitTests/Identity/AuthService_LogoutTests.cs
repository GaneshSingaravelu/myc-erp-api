using ERP.Identity.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for AuthService.LogoutAsync — covers session revocation,
/// idempotent behavior, and JTI cache invalidation.
/// </summary>
public class AuthService_LogoutTests : AuthServiceTestBase
{
    [Fact]
    public async Task Logout_WhenSessionNotFound_ReturnsSuccess()
    {
        // No session exists — logout should be idempotent (no error)
        var result = await Sut.LogoutAsync(Guid.NewGuid(), "nonexistent-refresh-token");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Logout_WhenSessionAlreadyRevoked_ReturnsSuccess()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", isRevoked: true);
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        // Already revoked — should return success (idempotent)
        var result = await Sut.LogoutAsync(user.Id, "my-refresh");

        Assert.True(result.IsSuccess);
        // Cache.Remove should NOT be called — session was already revoked, query returns null
        CacheMock.Verify(c => c.Remove(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Logout_WhenSessionActive_RevokesAndInvalidatesJti()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "active-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var result = await Sut.LogoutAsync(user.Id, "my-refresh");

        Assert.True(result.IsSuccess);

        // Session must be revoked
        var updatedSession = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updatedSession!.IsRevoked);
        Assert.NotNull(updatedSession.RevokedAt);
        Assert.Equal("Logout", updatedSession.RevokedReason);

        // JTI cache must be invalidated
        CacheMock.Verify(c => c.Remove("jti_valid:active-jti"), Times.Once);
    }

    [Fact]
    public async Task Logout_WhenWrongUserId_ReturnsSuccessWithoutRevoking()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "active-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        // Wrong user ID — session won't be found
        var result = await Sut.LogoutAsync(Guid.NewGuid(), "my-refresh");

        Assert.True(result.IsSuccess);

        // Session should remain active
        var unchangedSession = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.False(unchangedSession!.IsRevoked);
    }
}
