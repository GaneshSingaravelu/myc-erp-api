using ERP.Application.Abstractions.Email;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Abstractions;
using ERP.Identity.Configuration;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// R2-DEV-03A — versioned device sessions, consumed-token history and reuse classification
/// (AuthService, UNIT level, EF InMemory).
///
/// These tests pin the LOGIC: digest storage, duplicate-vs-reuse classification bound to the
/// consumed access-token JTI, family-only revocation, and that arbitrary or mismatched tokens
/// never revoke anything. EF InMemory honours concurrency tokens but has NO transactions, so
/// atomic commit/rollback and real contention are verified only in
/// RealProvider_RefreshConcurrencyTests against SQL Server and PostgreSQL.
/// </summary>
public class AuthService_RefreshRotationTests : AuthServiceTestBase
{
    private const string Password = "Password@123";

    private int _tokenCounter;

    public AuthService_RefreshRotationTests()
    {
        // Distinct refresh tokens per issuance, so rotations do not collide on the digest index.
        TokenServiceMock.Setup(t => t.GenerateRefreshToken()).Returns(() => $"rt-{++_tokenCounter}");
    }

    private void PresentPair(AppUser user, Tenant tenant, string jti) =>
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti));

    private async Task<UserSession> SeedSessionAsync(AppUser user, Tenant tenant, string refresh, string jti)
    {
        var session = CreateTestSession(user.Id, tenant.Id, refresh, jti);
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();
        return session;
    }

    private Task<UserSession> ReloadAsync(Guid id) =>
        Context.Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == id);

    /// <summary>Rotates <paramref name="refresh"/> (paired with <paramref name="jti"/>) and returns the new pair.</summary>
    private async Task<(string Refresh, string Jti)> RotateAsync(AppUser user, Tenant tenant, Guid sessionId, string refresh, string jti)
    {
        PresentPair(user, tenant, jti);
        var result = await Sut.RefreshTokenAsync(new RefreshTokenRequest("access", refresh), tenant.Code);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        var session = await ReloadAsync(sessionId);
        return (result.Value.RefreshToken, session.AccessTokenJti);
    }

    private async Task AgeConsumptionAsync(string consumedRefresh, TimeSpan age)
    {
        var hash = TestRefreshTokenHasher.Hash(consumedRefresh);
        var record = await Context.Set<ConsumedRefreshToken>().SingleAsync(c => c.TokenHash == hash);
        record.ConsumedAt = DateTime.UtcNow - age;
        await Context.SaveChangesAsync();
    }

    // ── Storage ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_StoresOnlyTheRefreshTokenDigest()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();

        var login = await Sut.LoginAsync(new LoginRequest("testuser", Password), tenant.Code);

        var session = await Context.Set<UserSession>().AsNoTracking().SingleAsync();
        Assert.Equal(TestRefreshTokenHasher.Hash(login.Value.RefreshToken), session.RefreshTokenHash);
        Assert.NotEqual(login.Value.RefreshToken, session.RefreshTokenHash);
        Assert.Matches("^[0-9a-f]{64}$", session.RefreshTokenHash);
        Assert.Equal(0, session.RotationVersion);
    }

    [Fact]
    public async Task Rotation_RecordsConsumedPair_AndBumpsVersion()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant, "first", "jti-1");

        var (next, nextJti) = await RotateAsync(user, tenant, session.Id, "first", "jti-1");

        var stored = await ReloadAsync(session.Id);
        Assert.Equal(1, stored.RotationVersion);
        Assert.Equal(TestRefreshTokenHasher.Hash(next), stored.RefreshTokenHash);
        Assert.NotEqual("jti-1", nextJti);

        var consumed = await Context.Set<ConsumedRefreshToken>().AsNoTracking().SingleAsync();
        Assert.Equal(TestRefreshTokenHasher.Hash("first"), consumed.TokenHash);
        Assert.Equal("jti-1", consumed.AccessTokenJti);   // the pair that was consumed
        Assert.Equal(0, consumed.RotationVersion);        // version it was consumed at
        Assert.Equal(session.Id, consumed.SessionId);
        Assert.Equal(user.Id, consumed.UserId);
        Assert.Equal(stored.ExpiresAt, consumed.RetainUntil); // retained for the session's whole life
    }

    // ── Duplicate vs reuse (bound to the consumed access-token JTI) ──────────

    [Fact]
    public async Task Duplicate_WithinWindow_Rejected_FamilyNotRevoked_NewPairStillWorks()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant, "first", "jti-1");
        var (next, nextJti) = await RotateAsync(user, tenant, session.Id, "first", "jti-1");

        PresentPair(user, tenant, "jti-1");
        var duplicate = await Sut.RefreshTokenAsync(new RefreshTokenRequest("access", "first"), tenant.Code);

        Assert.True(duplicate.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", duplicate.Error.Code);
        Assert.False((await ReloadAsync(session.Id)).IsRevoked);
        await RotateAsync(user, tenant, session.Id, next, nextJti); // the winning pair is unaffected
    }

    [Fact]
    public async Task Reuse_AfterWindow_RevokesOnlyThatFamily()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var family = await SeedSessionAsync(user, tenant, "first", "jti-1");
        var otherDevice = await SeedSessionAsync(user, tenant, "device-2", "jti-d2");
        var otherTenant = CreateTestTenant("XYZ");
        await Context.Set<Tenant>().AddAsync(otherTenant);
        await Context.Set<UserTenant>().AddAsync(new UserTenant { UserId = user.Id, TenantId = otherTenant.Id, IsActive = true });
        await Context.SaveChangesAsync();
        var otherTenantSession = await SeedSessionAsync(user, otherTenant, "tenant-2", "jti-t2");

        var (next, nextJti) = await RotateAsync(user, tenant, family.Id, "first", "jti-1");
        await AgeConsumptionAsync("first", TimeSpan.FromSeconds(TestRefreshTokenSettings.DuplicateWindowSeconds + 1));

        PresentPair(user, tenant, "jti-1");
        var reuse = await Sut.RefreshTokenAsync(new RefreshTokenRequest("access", "first"), tenant.Code);

        Assert.True(reuse.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", reuse.Error.Code); // generic
        var revoked = await ReloadAsync(family.Id);
        Assert.True(revoked.IsRevoked);
        Assert.Equal("RefreshTokenReuse", revoked.RevokedReason);
        Assert.False(await IsJtiActiveAsync(nextJti)); // the current access token of the family dies too

        // The current (winning) refresh token of the family is dead as well.
        PresentPair(user, tenant, nextJti);
        Assert.True((await Sut.RefreshTokenAsync(new RefreshTokenRequest("access", next), tenant.Code)).IsFailure);

        // Other device and other tenant sessions are independent.
        Assert.False((await ReloadAsync(otherDevice.Id)).IsRevoked);
        Assert.False((await ReloadAsync(otherTenantSession.Id)).IsRevoked);
        await RotateAsync(user, tenant, otherDevice.Id, "device-2", "jti-d2");
    }

    [Fact]
    public async Task ConsumedDigest_WithWrongAccessTokenJti_IsNotTreatedAsReuse_NothingRevoked()
    {
        // Someone who knows (or guesses) a consumed refresh token but does not hold its paired,
        // signed access token must not be able to revoke the session.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant, "first", "jti-1");
        await RotateAsync(user, tenant, session.Id, "first", "jti-1");
        await AgeConsumptionAsync("first", TimeSpan.FromMinutes(5));

        PresentPair(user, tenant, "some-other-jti");
        var attempt = await Sut.RefreshTokenAsync(new RefreshTokenRequest("access", "first"), tenant.Code);

        Assert.True(attempt.IsFailure);
        Assert.False((await ReloadAsync(session.Id)).IsRevoked);
    }

    [Fact]
    public async Task ConsumedDigest_PresentedByAnotherUser_IsNotTreatedAsReuse_NothingRevoked()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant, "first", "jti-1");
        await RotateAsync(user, tenant, session.Id, "first", "jti-1");
        await AgeConsumptionAsync("first", TimeSpan.FromMinutes(5));

        // Same JTI value, but a different user's signed token (sub ≠ consumed.UserId).
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(CreatePrincipal(Guid.NewGuid(), tenant.Code, user.SecurityStamp, "jti-1"));
        var attempt = await Sut.RefreshTokenAsync(new RefreshTokenRequest("access", "first"), tenant.Code);

        Assert.True(attempt.IsFailure);
        Assert.False((await ReloadAsync(session.Id)).IsRevoked);
    }

    [Fact]
    public async Task ArbitraryUnknownToken_Rejected_NothingRevoked()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var a = await SeedSessionAsync(user, tenant, "a", "jti-a");
        var b = await SeedSessionAsync(user, tenant, "b", "jti-b");

        PresentPair(user, tenant, "jti-a");
        var attempt = await Sut.RefreshTokenAsync(new RefreshTokenRequest("access", "never-issued"), tenant.Code);

        Assert.True(attempt.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", attempt.Error.Code);
        Assert.False((await ReloadAsync(a.Id)).IsRevoked);
        Assert.False((await ReloadAsync(b.Id)).IsRevoked);
    }

    [Fact]
    public async Task ReuseOfAlreadyRevokedFamily_IsRejected_AndKeepsOriginalReason()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant, "first", "jti-1");
        var (next, _) = await RotateAsync(user, tenant, session.Id, "first", "jti-1");
        Assert.True((await Sut.LogoutAsync(user.Id, next)).IsSuccess);
        await AgeConsumptionAsync("first", TimeSpan.FromMinutes(5));

        PresentPair(user, tenant, "jti-1");
        Assert.True((await Sut.RefreshTokenAsync(new RefreshTokenRequest("access", "first"), tenant.Code)).IsFailure);

        Assert.Equal("Logout", (await ReloadAsync(session.Id)).RevokedReason);
    }

    // ── Key management ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("REPLACE_ME — set via environment variable RefreshTokenSettings__HmacKey (identical on every instance)")]
    public void Settings_MissingPlaceholderOrShortKey_FailClosed(string? key)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RefreshTokenSettings.Validate(new RefreshTokenSettings { HmacKey = key! }));
        Assert.Throws<InvalidOperationException>(() =>
            new RefreshTokenHasher(Options.Create(new RefreshTokenSettings { HmacKey = key! })));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Settings_WindowOutOfRange_FailClosed(int seconds) =>
        Assert.Throws<InvalidOperationException>(() => RefreshTokenSettings.Validate(new RefreshTokenSettings
        {
            HmacKey = new string('k', 32), DuplicateWindowSeconds = seconds,
        }));

    [Fact]
    public void Hasher_IsDeterministicPerKey_AndKeyed()
    {
        var a = new RefreshTokenHasher(Options.Create(new RefreshTokenSettings { HmacKey = new string('a', 32) }));
        var a2 = new RefreshTokenHasher(Options.Create(new RefreshTokenSettings { HmacKey = new string('a', 32) }));
        var b = new RefreshTokenHasher(Options.Create(new RefreshTokenSettings { HmacKey = new string('b', 32) }));

        Assert.Equal(a.Hash("token"), a2.Hash("token"));    // stable across instances/restarts
        Assert.NotEqual(a.Hash("token"), b.Hash("token"));  // depends on the key
        Assert.NotEqual(a.Hash("token"), a.Hash("token2"));
        Assert.Matches("^[0-9a-f]{64}$", a.Hash("token"));
    }

    [Fact]
    public async Task KeyRotation_MakesExistingTokensUnmatchable_NoFallback()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        await SeedSessionAsync(user, tenant, "first", "jti-1");
        var rotatedKey = new RefreshTokenSettings { HmacKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)) };
        var afterRotation = new AuthService(new UserRepository(Context), new UserSessionRepository(Context),
            new UserPasswordHistoryRepository(Context), new TenantRepository(Context), new MasterUnitOfWork(Context),
            TokenServiceMock.Object, new RefreshTokenHasher(Options.Create(rotatedKey)), new Mock<IEmailService>().Object,
            Options.Create(TestJwtSettings), Options.Create(rotatedKey), NullLogger<AuthService>.Instance);

        PresentPair(user, tenant, "jti-1");
        var result = await afterRotation.RefreshTokenAsync(new RefreshTokenRequest("access", "first"), tenant.Code);

        Assert.True(result.IsFailure); // every user must log in again after a key rotation
    }

    // ── Stale writes cannot reverse a revocation ─────────────────────────────

    [Fact]
    public async Task StaleRotation_AfterRevocation_FailsAndDoesNotUnrevoke()
    {
        // Load the session in a SECOND context (stale copy), revoke it through Sut, then try to
        // save a rotation from the stale copy: the RotationVersion check must reject it.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = await SeedSessionAsync(user, tenant, "first", "jti-1");

        await using var staleContext = new ERP.Persistence.Context.MasterDbContext(
            new DbContextOptionsBuilder<ERP.Persistence.Context.MasterDbContext>()
                .UseInMemoryDatabase(InMemoryDatabaseName).Options);
        var stale = await staleContext.Set<UserSession>().SingleAsync(s => s.Id == session.Id);

        Assert.True((await Sut.LogoutAsync(user.Id, "first")).IsSuccess);

        stale.RefreshTokenHash = TestRefreshTokenHasher.Hash("stolen-rotation");
        stale.RotationVersion++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());

        var final = await ReloadAsync(session.Id);
        Assert.True(final.IsRevoked);
        Assert.Equal(TestRefreshTokenHasher.Hash("first"), final.RefreshTokenHash);
    }

    // "A revocation whose copy went stale re-reads and still wins" is NOT tested here: EF InMemory
    // has no transactions, so the failed first SaveChanges leaves other rows (e.g. the password
    // history insert) applied and the retry collides with them. On SQL Server/PostgreSQL the failed
    // attempt rolls back entirely. That race is verified in RealProvider_RefreshConcurrencyTests
    // (refresh vs logout and refresh vs password change, with deterministic barriers).
}
