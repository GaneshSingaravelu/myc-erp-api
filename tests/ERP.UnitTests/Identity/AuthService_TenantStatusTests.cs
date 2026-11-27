using ERP.Application.Abstractions.Email;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Abstractions;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Services;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// R2-DEV-01 — Inactive-tenant authentication.
///
/// Login and refresh must both refuse a tenant that is missing or inactive, refresh must
/// re-check tenant membership, and the refreshed token's tenant must come only from the
/// verified session tenant.
///
/// The concurrency tests each verify ONE deterministic interleaving, forced through mock
/// callbacks, using two independent DbContexts over a shared EF Core InMemory store. They pin
/// the last-writer-wins rotation outcome for that interleaving (at most one token pair stays
/// usable; a deactivation committed before a later refresh is seen by that refresh). They do
/// NOT establish behaviour for all possible interleavings, and EF InMemory has no transactions,
/// isolation levels or row locking, so they say nothing about SQL Server or PostgreSQL
/// concurrency behaviour.
/// </summary>
public class AuthService_TenantStatusTests : AuthServiceTestBase
{
    private const string Password = "Password@123";

    private void SetupPrincipal(AppUser user, Tenant tenant, string jti) =>
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti));

    private async Task<UserSession> SeedSessionAsync(AppUser user, Tenant tenant, string refresh = "my-refresh", string jti = "test-jti")
    {
        var session = CreateTestSession(user.Id, tenant.Id, refresh, jti);
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();
        return session;
    }

    // ─── Login ───

    [Fact]
    public async Task Login_ActiveTenant_Succeeds()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();

        var result = await Sut.LoginAsync(new LoginRequest("testuser", Password), tenant.Code);

        Assert.True(result.IsSuccess);
        Assert.Single(await Context.Set<UserSession>().ToListAsync());
    }

    [Fact]
    public async Task Login_InactiveTenant_FailsAndCreatesNoSession()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();
        tenant.IsActive = false;
        await Context.SaveChangesAsync();

        var result = await Sut.LoginAsync(new LoginRequest("testuser", Password), tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.TenantInactive", result.Error.Code);
        Assert.Empty(await Context.Set<UserSession>().ToListAsync());
    }

    [Fact]
    public async Task Login_InactiveTenantAddressedByGuid_Fails()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();
        tenant.IsActive = false;
        await Context.SaveChangesAsync();

        var result = await Sut.LoginAsync(new LoginRequest("testuser", Password), tenant.Id.ToString());

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.TenantInactive", result.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-tenant")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public async Task Login_MissingOrUnknownTenant_Fails(string tenantId)
    {
        await SeedUserWithRoleAsync();

        var result = await Sut.LoginAsync(new LoginRequest("testuser", Password), tenantId);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidTenant", result.Error.Code);
        Assert.Empty(await Context.Set<UserSession>().ToListAsync());
    }

    [Fact]
    public async Task Login_InactiveMembership_FailsWithGenericError()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var membership = await Context.Set<UserTenant>().SingleAsync(ut => ut.UserId == user.Id && ut.TenantId == tenant.Id);
        membership.IsActive = false;
        await Context.SaveChangesAsync();

        var result = await Sut.LoginAsync(new LoginRequest("testuser", Password), tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidCredentials", result.Error.Code);
    }

    [Fact]
    public async Task Login_ActiveTenantUserIsNotMemberOf_Fails_CrossTenantIsolation()
    {
        var (_, _, _, _) = await SeedUserWithRoleAsync();
        var other = CreateTestTenant("XYZ");
        await Context.Set<Tenant>().AddAsync(other);
        await Context.SaveChangesAsync();

        var result = await Sut.LoginAsync(new LoginRequest("testuser", Password), other.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidCredentials", result.Error.Code);
    }

    [Fact]
    public async Task Login_InactiveTenantUserIsNotMemberOf_ReturnsGenericError_NoStatusDisclosure()
    {
        await SeedUserWithRoleAsync();
        var other = CreateTestTenant("XYZ");
        other.IsActive = false;
        await Context.Set<Tenant>().AddAsync(other);
        await Context.SaveChangesAsync();

        var result = await Sut.LoginAsync(new LoginRequest("testuser", Password), other.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidCredentials", result.Error.Code);
        Assert.Empty(await Context.Set<UserSession>().ToListAsync());
    }

    [Fact]
    public async Task Login_InactiveTenantWrongPassword_ReturnsGenericError_CountsFailure_NoStatusDisclosure()
    {
        // Existing member of an existing (but inactive) tenant, wrong password.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        tenant.IsActive = false;
        await Context.SaveChangesAsync();
        var failuresBefore = user.AccessFailedCount;

        var result = await Sut.LoginAsync(new LoginRequest("testuser", "Wrong@Password1"), tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidCredentials", result.Error.Code);
        Assert.Equal("Invalid username or password.", result.Error.Message);
        Assert.DoesNotContain("inactive", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        var stored = await Context.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(failuresBefore + 1, stored.AccessFailedCount);
        Assert.False(stored.IsLocked); // one failure is below the 5-attempt lockout threshold
        Assert.Empty(await Context.Set<UserSession>().ToListAsync());
    }

    // ─── Refresh ───

    [Fact]
    public async Task Refresh_ActiveTenant_Succeeds_AndUsesSessionTenantCode()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        await SeedSessionAsync(user, tenant);
        SetupPrincipal(user, tenant, "test-jti");

        var result = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenant.Code.ToUpperInvariant());

        Assert.True(result.IsSuccess);
        TokenServiceMock.Verify(t => t.GenerateAccessToken(
            It.IsAny<AppUser>(), It.IsAny<IList<string>>(), It.IsAny<IList<string>>(),
            tenant.Code, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_InactiveTenant_FailsAndRevokesSession()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant);
        tenant.IsActive = false;
        await Context.SaveChangesAsync();
        SetupPrincipal(user, tenant, "test-jti");

        var result = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.TenantInactive", result.Error.Code);
        var updated = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updated!.IsRevoked);
        Assert.Equal("TenantInactive", updated.RevokedReason);
        Assert.False(await IsJtiActiveAsync("test-jti")); // R2-DEV-03A: no JTI cache; the session check rejects it
        TokenServiceMock.Verify(t => t.GenerateRefreshToken(), Times.Never);
    }

    [Fact]
    public async Task Refresh_AfterTenantReactivated_RevokedSessionStaysRevoked()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        await SeedSessionAsync(user, tenant);
        tenant.IsActive = false;
        await Context.SaveChangesAsync();
        SetupPrincipal(user, tenant, "test-jti");
        await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenant.Code);

        tenant.IsActive = true;
        await Context.SaveChangesAsync();
        var result = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refresh_MissingTenant_Fails(string tenantId)
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant);
        SetupPrincipal(user, tenant, "test-jti");

        var result = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenantId);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidTenant", result.Error.Code);
        TokenServiceMock.Verify(t => t.GenerateRefreshToken(), Times.Never);
        Assert.False((await Context.Set<UserSession>().FindAsync(session.Id))!.IsRevoked);
    }

    [Fact]
    public async Task Refresh_UnknownTenant_FailsAndRevokesSession()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant);
        SetupPrincipal(user, tenant, "test-jti");

        var result = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), "no-such-tenant");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.TenantMismatch", result.Error.Code);
        Assert.True((await Context.Set<UserSession>().FindAsync(session.Id))!.IsRevoked);
    }

    [Fact]
    public async Task Refresh_MembershipRevoked_FailsAndRevokesSession()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant);
        var membership = await Context.Set<UserTenant>().SingleAsync(ut => ut.UserId == user.Id);
        membership.IsActive = false;
        await Context.SaveChangesAsync();
        SetupPrincipal(user, tenant, "test-jti");

        var result = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);
        var updated = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updated!.IsRevoked);
        Assert.Equal("TenantMembershipRevoked", updated.RevokedReason);
    }

    [Fact]
    public async Task Refresh_SessionFromTenantA_PresentedForActiveTenantB_IsRejected_CrossTenantIsolation()
    {
        var (user, tenantA, _, _) = await SeedUserWithRoleAsync();
        var tenantB = CreateTestTenant("XYZ");
        await Context.Set<Tenant>().AddAsync(tenantB);
        await Context.Set<UserTenant>().AddAsync(new UserTenant { UserId = user.Id, TenantId = tenantB.Id, IsActive = true });
        await Context.SaveChangesAsync();
        var session = await SeedSessionAsync(user, tenantA);
        SetupPrincipal(user, tenantA, "test-jti");

        var result = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenantB.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.TenantMismatch", result.Error.Code);
        Assert.True((await Context.Set<UserSession>().FindAsync(session.Id))!.IsRevoked);
    }

    // ─── Session expiry / revocation preserved ───

    [Fact]
    public async Task Refresh_ExpiredOrRevokedSession_StillRejected_WithActiveTenant()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        await Context.Set<UserSession>().AddRangeAsync(
            CreateTestSession(user.Id, tenant.Id, "expired-refresh", "j1", expiresAt: DateTime.UtcNow.AddMinutes(-1)),
            CreateTestSession(user.Id, tenant.Id, "revoked-refresh", "j2", isRevoked: true));
        await Context.SaveChangesAsync();

        SetupPrincipal(user, tenant, "j1");
        var expired = await Sut.RefreshTokenAsync(new RefreshTokenRequest("x", "expired-refresh"), tenant.Code);
        SetupPrincipal(user, tenant, "j2");
        var revoked = await Sut.RefreshTokenAsync(new RefreshTokenRequest("x", "revoked-refresh"), tenant.Code);

        Assert.Equal("Auth.InvalidRefreshToken", expired.Error.Code);
        Assert.Equal("Auth.InvalidRefreshToken", revoked.Error.Code);
    }

    [Fact]
    public async Task Refresh_RotatedOutRefreshToken_CannotBeReplayed()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        await SeedSessionAsync(user, tenant);
        SetupPrincipal(user, tenant, "test-jti");
        TokenServiceMock.Setup(t => t.GenerateRefreshToken()).Returns("rotated-refresh");

        var first = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenant.Code);
        var replay = await Sut.RefreshTokenAsync(new RefreshTokenRequest("expired", "my-refresh"), tenant.Code);

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", replay.Error.Code);
    }

    // ─── Concurrency (deterministic interleaving across two independent DbContexts) ───

    /// <summary>
    /// Builds an AuthService over its OWN MasterDbContext on a shared InMemory store, so two
    /// services behave like two concurrent requests with separate change trackers.
    /// </summary>
    private static (AuthService Service, MasterDbContext Ctx, Mock<ITokenService> Tokens) BuildIndependentService(string dbName)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());
        var ctx = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new AuditableEntityInterceptor(currentUser.Object))
            .Options);
        var tokens = new Mock<ITokenService>();
        tokens.Setup(t => t.GenerateAccessToken(It.IsAny<AppUser>(), It.IsAny<IList<string>>(),
            It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<string>())).Returns("access");
        var service = new AuthService(new UserRepository(ctx), new UserSessionRepository(ctx),
            new UserPasswordHistoryRepository(ctx), new TenantRepository(ctx), new MasterUnitOfWork(ctx),
            tokens.Object, TestRefreshTokenHasher, new Mock<IEmailService>().Object,
            Options.Create(TestJwtSettings), Options.Create(TestRefreshTokenSettings), NullLogger<AuthService>.Instance);
        return (service, ctx, tokens);
    }

    private static async Task<(AppUser User, Tenant Tenant, Guid SessionId)> SeedSharedAsync(MasterDbContext ctx)
    {
        var user = new AppUser
        {
            UserName = "racer", NormalizedUserName = "RACER", Email = "r@example.com", NormalizedEmail = "R@EXAMPLE.COM",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4), FirstName = "R", LastName = "U",
            IsActive = true, SecurityStamp = Guid.NewGuid().ToString()
        };
        var tenant = new Tenant { Code = TenantCode.Normalize("RACE"), Name = "Race", ConnectionString = "x", DbProvider = "SqlServer", IsActive = true, CreatedAt = DateTime.UtcNow };
        await ctx.Set<AppUser>().AddAsync(user);
        await ctx.Set<Tenant>().AddAsync(tenant);
        await ctx.SaveChangesAsync();
        await ctx.Set<UserTenant>().AddAsync(new UserTenant { UserId = user.Id, TenantId = tenant.Id, IsActive = true });
        var session = new UserSession
        {
            UserId = user.Id, TenantId = tenant.Id, RefreshTokenHash = TestRefreshTokenHasher.Hash("shared-refresh"), AccessTokenJti = "shared-jti",
            RefreshTokenExpiry = DateTime.UtcNow.AddDays(7), ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow
        };
        await ctx.Set<UserSession>().AddAsync(session);
        await ctx.SaveChangesAsync();
        return (user, tenant, session.Id);
    }

    [Fact]
    public async Task ConcurrentRefresh_SameRefreshToken_ExactlyOneRotationWins_LoserOverwritesNothing()
    {
        // R2-DEV-03A changed this contract. Before, the session had no concurrency token and the
        // LAST writer won: both racers "succeeded" and the loser's pair silently died. Now the
        // session UPDATE is guarded by RotationVersion, so the request that saves second fails
        // and writes nothing — it can neither overwrite the winner nor mint a second pair.
        // EF InMemory honours concurrency tokens; real SQL Server/PostgreSQL behaviour is covered
        // by RealProvider_RefreshConcurrencyTests (not by this test).
        var db = Guid.NewGuid().ToString();
        var (a, ctxA, tokensA) = BuildIndependentService(db);
        var (b, ctxB, tokensB) = BuildIndependentService(db);
        var (verify, ctxV, verifyTokens) = BuildIndependentService(db);
        var (user, tenant, sessionId) = await SeedSharedAsync(ctxA);
        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, "shared-jti");
        tokensA.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>())).Returns(principal);
        tokensB.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>())).Returns(principal);
        tokensB.Setup(t => t.GenerateRefreshToken()).Returns("refresh-B");

        // Interleave: A has passed every check and loaded the session; B completes a full
        // refresh with the same token before A saves.
        Result<LoginResponse>? resultB = null;
        tokensA.Setup(t => t.GenerateRefreshToken()).Returns(() =>
        {
            resultB = b.RefreshTokenAsync(new RefreshTokenRequest("x", "shared-refresh"), tenant.Code).GetAwaiter().GetResult();
            return "refresh-A";
        });

        var resultA = await a.RefreshTokenAsync(new RefreshTokenRequest("x", "shared-refresh"), tenant.Code);

        // B saved first and won; A lost the optimistic-concurrency check and got nothing.
        Assert.NotNull(resultB);
        Assert.True(resultB!.IsSuccess);
        Assert.True(resultA.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", resultA.Error.Code);

        var stored = await ctxV.Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(TestRefreshTokenHasher.Hash("refresh-B"), stored.RefreshTokenHash); // winner's token intact
        Assert.Equal(1, stored.RotationVersion);
        Assert.False(stored.IsRevoked);
        Assert.Single(await ctxV.Set<UserSession>().AsNoTracking().ToListAsync()); // no extra session minted
        // NOT asserted here: that the loser's consumed-token insert was rolled back. EF InMemory
        // has no transactions, so a SaveChanges that fails on one entry may already have applied
        // another. Atomic commit/rollback of rotation + history is verified only against real
        // SQL Server and PostgreSQL (RealProvider_RefreshConcurrencyTests).

        // The original pair, re-sent immediately, is a DUPLICATE: rejected, family not revoked.
        verifyTokens.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>())).Returns(principal);
        var replayOriginal = await verify.RefreshTokenAsync(new RefreshTokenRequest("x", "shared-refresh"), tenant.Code);
        Assert.True(replayOriginal.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", replayOriginal.Error.Code);

        // The loser's token was never stored, so it is simply unknown.
        var replayLoser = await verify.RefreshTokenAsync(new RefreshTokenRequest("x", "refresh-A"), tenant.Code);
        Assert.True(replayLoser.IsFailure);

        var afterReplays = await ctxV.Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.False(afterReplays.IsRevoked);
        Assert.Equal(TestRefreshTokenHasher.Hash("refresh-B"), afterReplays.RefreshTokenHash);

        ctxA.Dispose(); ctxB.Dispose(); ctxV.Dispose();
    }

    [Fact]
    public async Task TenantDeactivatedDuringInFlightRefresh_NextRefreshIsRejected()
    {
        var db = Guid.NewGuid().ToString();
        var (a, ctxA, tokensA) = BuildIndependentService(db);
        var (admin, ctxAdmin, _) = BuildIndependentService(db);
        var (next, ctxNext, tokensNext) = BuildIndependentService(db);
        var (user, tenant, sessionId) = await SeedSharedAsync(ctxA);

        tokensA.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, "shared-jti"));
        // Deactivate the tenant from an independent context while A is mid-rotation.
        tokensA.Setup(t => t.GenerateRefreshToken()).Returns(() =>
        {
            var t = ctxAdmin.Set<Tenant>().Single(x => x.Id == tenant.Id);
            t.IsActive = false;
            ctxAdmin.SaveChanges();
            return "refresh-A";
        });
        var inFlight = await a.RefreshTokenAsync(new RefreshTokenRequest("x", "shared-refresh"), tenant.Code);
        Assert.True(inFlight.IsSuccess); // TOCTOU window: check passed before deactivation committed

        var stored = await ctxNext.Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        tokensNext.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, stored.AccessTokenJti));
        tokensNext.Setup(t => t.GenerateRefreshToken()).Returns("refresh-next");

        var after = await next.RefreshTokenAsync(new RefreshTokenRequest("x", "refresh-A"), tenant.Code);

        Assert.True(after.IsFailure);
        Assert.Equal("Auth.TenantInactive", after.Error.Code);
        var final = await ctxNext.Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.True(final.IsRevoked);
        Assert.Equal("TenantInactive", final.RevokedReason);

        ctxA.Dispose(); ctxAdmin.Dispose(); ctxNext.Dispose();
    }

    [Fact]
    public async Task LoginRacingTenantDeactivation_SessionCannotBeRefreshed()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        // Login completes against an active tenant…
        var login = await Sut.LoginAsync(new LoginRequest("testuser", Password), tenant.Code);
        Assert.True(login.IsSuccess);
        var session = await Context.Set<UserSession>().SingleAsync();

        // …and the tenant is deactivated immediately afterwards.
        tenant.IsActive = false;
        await Context.SaveChangesAsync();
        SetupPrincipal(user, tenant, session.AccessTokenJti);

        var refresh = await Sut.RefreshTokenAsync(new RefreshTokenRequest("x", login.Value.RefreshToken), tenant.Code);

        Assert.True(refresh.IsFailure);
        Assert.Equal("Auth.TenantInactive", refresh.Error.Code);
        Assert.True((await Context.Set<UserSession>().SingleAsync()).IsRevoked);
    }
}
