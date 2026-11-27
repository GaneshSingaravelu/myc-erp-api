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
        // Already revoked: nothing is written again — reason, time and version are unchanged.
        var stored = await Context.Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.True(stored.IsRevoked);
        Assert.Equal(session.RotationVersion, stored.RotationVersion);
        Assert.Equal(session.RevokedReason, stored.RevokedReason);
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
        Assert.False(await IsJtiActiveAsync("active-jti")); // R2-DEV-03A: no JTI cache; the session check rejects it
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
