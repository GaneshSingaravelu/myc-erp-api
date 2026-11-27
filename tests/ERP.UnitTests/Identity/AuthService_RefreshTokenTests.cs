using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// Unit tests for AuthService.RefreshTokenAsync — the most complex method.
/// Covers token parsing, session lookup, tenant binding, SecurityStamp validation,
/// token rotation, and JTI cache invalidation.
/// </summary>
public class AuthService_RefreshTokenTests : AuthServiceTestBase
{
    // ─── Token Parsing Failures ───

    [Fact]
    public async Task Refresh_WhenTokenInvalid_ReturnsInvalidToken()
    {
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns((ClaimsPrincipal?)null);

        var request = new RefreshTokenRequest("bad-token", "some-refresh");

        var result = await Sut.RefreshTokenAsync(request, "ABC");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidToken", result.Error.Code);
    }

    [Fact]
    public async Task Refresh_WhenUserIdClaimMissing_ReturnsInvalidToken()
    {
        // Principal with no NameIdentifier claim
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "Test"));
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "some-refresh");

        var result = await Sut.RefreshTokenAsync(request, "ABC");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidToken", result.Error.Code);
    }

    [Fact]
    public async Task Refresh_WhenUserIdClaimNotGuid_ReturnsInvalidToken()
    {
        // Use the JWT-native short name "sub" — production reads via that name now
        // (TokenService disables inbound claim mapping). Using ClaimTypes.NameIdentifier
        // here would silently make the test exercise the missing-claim branch instead
        // of the not-a-guid branch.
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "not-a-guid") };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "some-refresh");

        var result = await Sut.RefreshTokenAsync(request, "ABC");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidToken", result.Error.Code);
    }

    // ─── Session Not Found ───

    [Fact]
    public async Task Refresh_WhenSessionNotFound_ReturnsInvalidRefreshToken()
    {
        var userId = Guid.NewGuid();
        var principal = CreatePrincipal(userId);
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "wrong-refresh-token");

        var result = await Sut.RefreshTokenAsync(request, Guid.NewGuid().ToString());

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);
    }

    [Fact]
    public async Task Refresh_WhenSessionRevoked_ReturnsInvalidRefreshToken()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", isRevoked: true);
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "test-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "my-refresh");

        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);
    }

    [Fact]
    public async Task Refresh_WhenRefreshTokenExpired_ReturnsInvalidRefreshToken()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh",
            refreshTokenExpiry: DateTime.UtcNow.AddDays(-1)); // Expired yesterday
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "test-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "my-refresh");

        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);
    }

    [Fact]
    public async Task Refresh_WhenAbsoluteSessionExpired_ReturnsInvalidRefreshToken()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh",
            expiresAt: DateTime.UtcNow.AddDays(-1)); // Absolute expiry passed
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "test-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "my-refresh");

        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);
    }

    // ─── Tenant Binding ───

    [Fact]
    public async Task Refresh_WhenTenantMismatch_RevokesSessionAndReturnsError()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var otherTenant = CreateTestTenant("XYZ");
        await Context.Set<Tenant>().AddAsync(otherTenant);
        await Context.SaveChangesAsync();

        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "test-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "test-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        // Request refresh with DIFFERENT tenant
        var request = new RefreshTokenRequest("expired-token", "my-refresh");
        var result = await Sut.RefreshTokenAsync(request, otherTenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.TenantMismatch", result.Error.Code);

        // Session must be revoked
        var updatedSession = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updatedSession!.IsRevoked);
        Assert.Equal("TenantMismatch", updatedSession.RevokedReason);

        // JTI cache must be invalidated
        Assert.False(await IsJtiActiveAsync("test-jti")); // R2-DEV-03A: no JTI cache; the session check rejects it
    }

    [Fact]
    public async Task Refresh_WhenRequestedTenantNotExists_RevokesSession()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "test-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "test-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        // Nonexistent tenant code
        var request = new RefreshTokenRequest("expired-token", "my-refresh");
        var result = await Sut.RefreshTokenAsync(request, "NONEXISTENT");

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.TenantMismatch", result.Error.Code);
    }

    [Fact]
    public async Task Refresh_WhenTokenTenantClaimMismatch_RevokesSession()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var otherTenant = CreateTestTenant("XYZ");
        await Context.Set<Tenant>().AddAsync(otherTenant);
        await Context.SaveChangesAsync();

        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "test-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        // Token's tenant_id claim points to a DIFFERENT tenant than the session
        var principal = CreatePrincipal(user.Id, tenantClaim: otherTenant.Code, securityStamp: user.SecurityStamp, jti: "test-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "my-refresh");
        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.TenantMismatch", result.Error.Code);

        var updatedSession = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updatedSession!.IsRevoked);
        Assert.Equal("TokenTenantMismatch", updatedSession.RevokedReason);
    }

    // ─── User Validation ───

    [Fact]
    public async Task Refresh_WhenUserInactive_RevokesSessionAndReturnsError()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "test-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        // Deactivate user after session was created
        user.IsActive = false;
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "test-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "my-refresh");
        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);

        var updatedSession = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updatedSession!.IsRevoked);
        Assert.Equal("UserInactive", updatedSession.RevokedReason);
    }

    // ─── SecurityStamp Validation ───

    [Fact]
    public async Task Refresh_WhenSecurityStampChanged_RevokesSession()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "test-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        // Token has OLD SecurityStamp, user has NEW one (password was changed)
        var principal = CreatePrincipal(user.Id, tenant.Code, securityStamp: "old-stamp-not-matching", jti: "test-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "my-refresh");
        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.SecurityStampChanged", result.Error.Code);

        var updatedSession = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updatedSession!.IsRevoked);
        Assert.Equal("SecurityStampChanged", updatedSession.RevokedReason);
        Assert.False(await IsJtiActiveAsync("test-jti")); // R2-DEV-03A: no JTI cache; the session check rejects it
    }

    [Fact]
    public async Task Refresh_WhenSecurityStampClaimAbsent_SkipsStampValidation()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "old-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        // No security_stamp claim — should skip validation and succeed
        var principal = CreatePrincipal(user.Id, tenant.Code, securityStamp: null, jti: "old-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "my-refresh");
        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsSuccess);
    }

    // ─── Successful Token Rotation ───

    [Fact]
    public async Task Refresh_WhenValid_RotatesTokens()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "old-refresh", jti: "old-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "old-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "old-refresh");
        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsSuccess);
        Assert.Equal("mock-access-token", result.Value.AccessToken);
        Assert.Equal("mock-refresh-token", result.Value.RefreshToken);

        // Session should have new tokens
        var updatedSession = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.Equal(TestRefreshTokenHasher.Hash("mock-refresh-token"), updatedSession!.RefreshTokenHash); // digest, not plaintext
        Assert.NotEqual("old-jti", updatedSession.AccessTokenJti);
        Assert.NotNull(updatedSession.LastAccessedAt);
        Assert.Equal(1, updatedSession.RotationVersion);
    }

    [Fact]
    public async Task Refresh_WhenValid_InvalidatesOldJti()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "old-refresh", jti: "old-jti-to-invalidate");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "old-jti-to-invalidate");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "old-refresh");
        await Sut.RefreshTokenAsync(request, tenant.Code);

        // Old JTI must be evicted from cache
        Assert.False(await IsJtiActiveAsync("old-jti-to-invalidate")); // R2-DEV-03A: no JTI cache; the session check rejects it
    }

    [Fact]
    public async Task Refresh_WhenValid_DoesNotExtendAbsoluteExpiry()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var absoluteExpiry = DateTime.UtcNow.AddDays(3); // Expires in 3 days
        var session = CreateTestSession(user.Id, tenant.Id, "old-refresh", jti: "old-jti",
            expiresAt: absoluteExpiry);
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "old-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "old-refresh");
        await Sut.RefreshTokenAsync(request, tenant.Code);

        // ExpiresAt should NOT be extended
        var updatedSession = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.Equal(absoluteExpiry, updatedSession!.ExpiresAt);
    }

    [Fact]
    public async Task Refresh_WhenValid_DerivesTenantFromSessionNotHeader()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "old-refresh", jti: "old-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "old-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "old-refresh");
        await Sut.RefreshTokenAsync(request, tenant.Code);

        // Verify the new access token uses the tenant Code from session, not the header
        TokenServiceMock.Verify(t => t.GenerateAccessToken(
            It.IsAny<AppUser>(), It.IsAny<IList<string>>(),
            It.IsAny<IList<string>>(), tenant.Code, It.IsAny<string>()), Times.Once);
    }

    // ─── JTI Binding (security review finding #2) ───

    [Fact]
    public async Task Refresh_WhenAccessTokenJtiMissing_RevokesSessionAndReturnsError()
    {
        // Without a jti claim, an attacker could present an arbitrary expired
        // token paired with a valid refresh token. The session MUST be revoked
        // and the refresh attempt rejected so the user has to re-authenticate.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "my-refresh", jti: "session-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        // Principal omits the jti claim entirely (jti: null → no claim added).
        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: null);
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "my-refresh");
        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);

        var updated = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updated!.IsRevoked);
        Assert.Equal("JtiMismatch", updated.RevokedReason);
        Assert.False(await IsJtiActiveAsync("session-jti")); // R2-DEV-03A: no JTI cache; the session check rejects it
    }

    // ─── Real-JWT Round-Trip Integration (security review follow-up) ───

    [Fact]
    public async Task Refresh_WithRealJwtRoundTrip_FindsJtiClaim()
    {
        // Why a real-JWT integration test (not just the manual-Claim ones above):
        //
        // The other refresh tests construct ClaimsPrincipal by hand with literal
        // claim names ("jti", "sub", ...). They never exercise the actual
        // JwtSecurityTokenHandler validation pipeline that production hits — so
        // they cannot catch a regression where the framework changes claim mapping
        // and "jti" silently shows up under a different claim type in the principal.
        //
        // This test wires AuthService against the REAL TokenService, generates a
        // genuine signed JWT, then runs it through the refresh flow end-to-end.
        // If the production read of jti ever fails to find the claim — for any
        // reason, including framework default changes — this test fails immediately.

        var realTokenService = new TokenService(Options.Create(TestJwtSettings));
        var sutWithRealTokens = new AuthService(
            new UserRepository(Context),
            new UserSessionRepository(Context),
            new UserPasswordHistoryRepository(Context),
            new TenantRepository(Context),
            new MasterUnitOfWork(Context),
            realTokenService,
            TestRefreshTokenHasher,
            EmailServiceMock.Object,
            Options.Create(TestJwtSettings),
            Options.Create(TestRefreshTokenSettings),
            NullLogger<AuthService>.Instance);

        var (user, tenant, role, permission) = await SeedUserWithRoleAsync();

        // Generate a real JWT with a known jti, then seed a session whose
        // AccessTokenJti matches it — exactly mirroring what LoginAsync does.
        var jti = Guid.NewGuid().ToString();
        var accessToken = realTokenService.GenerateAccessToken(
            user,
            new[] { role.Name },
            new[] { permission.Code },
            tenant.Code,
            jti);
        var refreshToken = realTokenService.GenerateRefreshToken();

        var session = CreateTestSession(user.Id, tenant.Id, refreshToken, jti);
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        var request = new RefreshTokenRequest(accessToken, refreshToken);
        var result = await sutWithRealTokens.RefreshTokenAsync(request, tenant.Code);

        // The whole point: refresh must succeed. If it returns Auth.InvalidRefreshToken
        // here, the jti read is broken (claim mapping or otherwise).
        Assert.True(
            result.IsSuccess,
            $"Refresh failed with {result.Error?.Code}: {result.Error?.Message}. " +
            "This usually means JwtSecurityTokenHandler claim mapping changed " +
            "and the jti read in AuthService.RefreshTokenAsync needs another fallback name.");

        // Sanity: session was actually rotated (proves we cleared the JTI gate
        // and went through the full happy path, not some other code branch).
        var rotated = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.NotEqual(jti, rotated!.AccessTokenJti);
        Assert.NotEqual(TestRefreshTokenHasher.Hash(refreshToken), rotated.RefreshTokenHash);
        Assert.False(rotated.IsRevoked);
    }

    [Fact]
    public async Task Refresh_WhenAccessTokenJtiDoesNotMatchSession_RevokesSessionAndReturnsError()
    {
        // Access token A from session-1 + refresh token B from session-2 (same user)
        // is the canonical mix-and-match attack. The presented jti != session.AccessTokenJti
        // must be detected, the session revoked, and the JTI cache invalidated.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var session = CreateTestSession(user.Id, tenant.Id, "session2-refresh", jti: "session2-jti");
        await Context.Set<UserSession>().AddAsync(session);
        await Context.SaveChangesAsync();

        // Principal carries jti from a DIFFERENT session.
        var principal = CreatePrincipal(user.Id, tenant.Code, user.SecurityStamp, jti: "session1-jti");
        TokenServiceMock.Setup(t => t.GetPrincipalFromExpiredToken(It.IsAny<string>()))
            .Returns(principal);

        var request = new RefreshTokenRequest("expired-token", "session2-refresh");
        var result = await Sut.RefreshTokenAsync(request, tenant.Code);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidRefreshToken", result.Error.Code);

        var updated = await Context.Set<UserSession>().FindAsync(session.Id);
        Assert.True(updated!.IsRevoked);
        Assert.Equal("JtiMismatch", updated.RevokedReason);
        Assert.False(await IsJtiActiveAsync("session2-jti")); // R2-DEV-03A: no JTI cache; the session check rejects it
    }
}
