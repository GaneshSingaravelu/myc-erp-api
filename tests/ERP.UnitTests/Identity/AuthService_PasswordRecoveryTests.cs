using System.Security.Cryptography;
using System.Text;
using ERP.Application.Abstractions.Email;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ERP.UnitTests.Identity;

/// <summary>
/// R2-DEV-02B-3A — Tenant-bound password recovery (AuthService, UNIT level).
///
/// AppUser is a global identity with one password. Recovery is offered through any ACTIVE
/// tenant in which the user holds an ACTIVE membership; the issued token is bound to that
/// tenant and can only be redeemed through it. Every refusal is indistinguishable to the
/// caller. Tests are named after the B3 acceptance criterion they cover.
///
/// Uses the shared InMemory MasterDbContext + real repositories from AuthServiceTestBase.
/// The HTTP pipeline (routing, model binding, TenantMiddleware, controller) is covered
/// separately in AuthController_PasswordRecoveryPipelineTests.
/// </summary>
public class AuthService_PasswordRecoveryTests : AuthServiceTestBase
{
    private const string Email = "testuser@example.com";
    private const string OldPassword = "Password@123";
    private const string NewPassword = "NewPassword@456";
    private const string Generic = "If that email is registered, a reset link has been sent.";

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Tenant> AddTenantAsync(string code, bool active = true)
    {
        var tenant = CreateTestTenant(code);
        tenant.IsActive = active;
        await Context.Set<Tenant>().AddAsync(tenant);
        await Context.SaveChangesAsync();
        return tenant;
    }

    private async Task AddMembershipAsync(AppUser user, Tenant tenant, bool active = true)
    {
        await Context.Set<UserTenant>().AddAsync(new UserTenant { UserId = user.Id, TenantId = tenant.Id, IsActive = active });
        await Context.SaveChangesAsync();
    }

    private async Task<AppUser> ReloadUserAsync(Guid id) =>
        await Context.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Id == id);

    /// <summary>Issues a token through <paramref name="tenantId"/> (exposeToken: true) and returns it.</summary>
    private async Task<string?> IssueAsync(string tenantId, string email = Email)
    {
        var result = await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(email), tenantId, exposeToken: true);
        Assert.True(result.IsSuccess);
        Assert.Equal(Generic, result.Value.Message);
        return result.Value.ResetToken;
    }

    private static string Sha256Hex(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    private Task<ERP.SharedKernel.Results.Result> RedeemAsync(string tenantId, string token, string email = Email, string newPassword = NewPassword) =>
        Sut.ResetPasswordAsync(new ResetPasswordRequest(email, token, newPassword), tenantId);

    private static void AssertGenericInvalid(ERP.SharedKernel.Results.Result result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("Auth.InvalidResetToken", result.Error.Code);
        Assert.Equal("Invalid or expired password reset token.", result.Error.Message);
    }

    // ── B3-01: active tenant + active membership → tenant-bound token, redeemable there ──

    [Fact]
    public async Task B3_01_ActiveTenantActiveMember_IssuesTenantBoundToken()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();

        var token = await IssueAsync(tenant.Code);

        Assert.False(string.IsNullOrEmpty(token));
        var stored = await ReloadUserAsync(user.Id);
        Assert.Equal(Sha256Hex($"erp-pwreset-v2:{tenant.Id:D}:{token}"), stored.PasswordResetTokenHash);
        Assert.NotEqual(Sha256Hex(token!), stored.PasswordResetTokenHash); // not the old unbound format
        Assert.Equal(64, stored.PasswordResetTokenHash!.Length);           // same column format
        Assert.InRange(stored.PasswordResetTokenExpiry!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(15.1));
    }

    [Fact]
    public async Task B3_01_TokensAreRandomPerIssuance()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();

        var first = await IssueAsync(tenant.Code);
        var second = await IssueAsync(tenant.Code);

        Assert.NotEqual(first, second);
        Assert.Equal(43, first!.Length); // 32 random bytes, base64url, unpadded
    }

    [Fact]
    public async Task B3_01_RedeemThroughSameTenant_Succeeds_ByCodeOrById()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var token = await IssueAsync(tenant.Code);

        // Same authoritative tenant addressed by its Id: the binding uses Tenant.Id, not the header text.
        var result = await RedeemAsync(tenant.Id.ToString(), token!);

        Assert.True(result.IsSuccess);
        var stored = await ReloadUserAsync(user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(NewPassword, stored.PasswordHash));
        Assert.Null(stored.PasswordResetTokenHash);
        Assert.Null(stored.PasswordResetTokenExpiry);
    }

    [Fact]
    public async Task B3_01_ProductionPath_EmailsTokenAndDoesNotExposeIt()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();

        var result = await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: false);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ResetToken);
        EmailServiceMock.Verify(e => e.SendAsync(It.Is<EmailMessage>(m => m.To == Email), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── B3-02: inactive / unrelated / missing / unknown tenant at issuance → nothing issued ──

    [Fact]
    public async Task B3_02_InactiveTenantAtIssuance_NoToken_ExistingTokenUntouched()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var existing = await IssueAsync(tenant.Code);
        var before = await ReloadUserAsync(user.Id);
        tenant.IsActive = false;
        await Context.SaveChangesAsync();

        var token = await IssueAsync(tenant.Code);

        Assert.Null(token);
        var after = await ReloadUserAsync(user.Id);
        Assert.Equal(before.PasswordResetTokenHash, after.PasswordResetTokenHash);
        Assert.Equal(before.PasswordResetTokenExpiry, after.PasswordResetTokenExpiry);
        Assert.NotNull(existing);
    }

    [Fact]
    public async Task B3_02_UnrelatedTenant_NoToken()
    {
        var (user, _, _, _) = await SeedUserWithRoleAsync();
        var unrelated = await AddTenantAsync("XYZ");

        var token = await IssueAsync(unrelated.Code);

        Assert.Null(token);
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Fact]
    public async Task B3_02_InactiveMembership_NoToken()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var membership = await Context.Set<UserTenant>().SingleAsync(ut => ut.UserId == user.Id && ut.TenantId == tenant.Id);
        membership.IsActive = false;
        await Context.SaveChangesAsync();

        Assert.Null(await IssueAsync(tenant.Code));
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-tenant")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public async Task B3_02_MissingOrUnknownTenantContext_NoToken(string tenantId)
    {
        var (user, _, _, _) = await SeedUserWithRoleAsync();

        Assert.Null(await IssueAsync(tenantId));
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
        EmailServiceMock.VerifyNoOtherCalls();
    }

    // ── B3-03: a token from tenant A can never be redeemed under tenant B ──

    [Fact]
    public async Task B3_03_TokenFromTenantA_RedeemedUnderTenantB_MemberOfBoth_Rejected()
    {
        var (user, tenantA, _, _) = await SeedUserWithRoleAsync();
        var tenantB = await AddTenantAsync("BBB");
        await AddMembershipAsync(user, tenantB);
        var token = await IssueAsync(tenantA.Code);

        var underB = await RedeemAsync(tenantB.Code, token!);

        AssertGenericInvalid(underB);
        var stored = await ReloadUserAsync(user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, stored.PasswordHash));
        Assert.NotNull(stored.PasswordResetTokenHash); // the failed cross-tenant attempt did not burn A's token

        // ...and it still works through the tenant it was issued for.
        Assert.True((await RedeemAsync(tenantA.Code, token!)).IsSuccess);
    }

    [Fact]
    public async Task B3_03_TokenFromTenantB_RedeemedUnderTenantA_Rejected_BindingIsTwoWay()
    {
        var (user, tenantA, _, _) = await SeedUserWithRoleAsync();
        var tenantB = await AddTenantAsync("BBB");
        await AddMembershipAsync(user, tenantB);
        var token = await IssueAsync(tenantB.Code);

        AssertGenericInvalid(await RedeemAsync(tenantA.Code, token!));
        Assert.True((await RedeemAsync(tenantB.Code, token!)).IsSuccess);
    }

    // ── B3-04: tenant deactivated after issuance ──

    [Fact]
    public async Task B3_04_TenantDeactivatedAfterIssuance_RedemptionRejected()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var token = await IssueAsync(tenant.Code);
        tenant.IsActive = false;
        await Context.SaveChangesAsync();

        AssertGenericInvalid(await RedeemAsync(tenant.Code, token!));
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, (await ReloadUserAsync(user.Id)).PasswordHash));
    }

    // ── B3-05: membership removed before redemption ──

    [Fact]
    public async Task B3_05_MembershipRemovedBeforeRedemption_Rejected()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var token = await IssueAsync(tenant.Code);
        var membership = await Context.Set<UserTenant>().SingleAsync(ut => ut.UserId == user.Id && ut.TenantId == tenant.Id);
        membership.IsActive = false;
        await Context.SaveChangesAsync();

        AssertGenericInvalid(await RedeemAsync(tenant.Code, token!));
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, (await ReloadUserAsync(user.Id)).PasswordHash));
    }

    // ── B3-06: multiple memberships; one inactive tenant does not block another ──

    [Fact]
    public async Task B3_06_MultipleActiveMemberships_RecoverThroughEither()
    {
        var (user, tenantA, _, _) = await SeedUserWithRoleAsync();
        var tenantB = await AddTenantAsync("BBB");
        await AddMembershipAsync(user, tenantB);

        var viaB = await IssueAsync(tenantB.Code);
        Assert.NotNull(viaB);
        Assert.True((await RedeemAsync(tenantB.Code, viaB!)).IsSuccess);

        var viaA = await IssueAsync(tenantA.Code);
        Assert.NotNull(viaA);
        Assert.True((await RedeemAsync(tenantA.Code, viaA!, newPassword: "Another@789")).IsSuccess);
    }

    [Fact]
    public async Task B3_06_InactiveTenantA_DoesNotBlockRecoveryThroughActiveTenantB()
    {
        var (user, tenantA, _, _) = await SeedUserWithRoleAsync();
        tenantA.IsActive = false;
        await Context.SaveChangesAsync();
        var tenantB = await AddTenantAsync("BBB");
        await AddMembershipAsync(user, tenantB);

        Assert.Null(await IssueAsync(tenantA.Code));
        var token = await IssueAsync(tenantB.Code);

        Assert.NotNull(token);
        Assert.True((await RedeemAsync(tenantB.Code, token!)).IsSuccess);
    }

    // ── B3-07: authoritative Master DB data; lookup failure fails closed ──

    private AuthService ServiceWithTenants(ITenantRepository tenants) => BuildService(tenants: tenants);

    /// <summary>An AuthService over the shared Context, with optional replacements for failure simulation.</summary>
    private AuthService BuildService(
        ITenantRepository? tenants = null, IMasterUnitOfWork? unitOfWork = null, ILogger<AuthService>? logger = null) => new(
        new UserRepository(Context), new UserSessionRepository(Context), new UserPasswordHistoryRepository(Context),
        tenants ?? new TenantRepository(Context), unitOfWork ?? new MasterUnitOfWork(Context),
        TokenServiceMock.Object, TestRefreshTokenHasher, EmailServiceMock.Object,
        Options.Create(TestJwtSettings), Options.Create(TestRefreshTokenSettings),logger ?? NullLogger<AuthService>.Instance);

    /// <summary>A provider-level failure as ADO.NET reports it (SqlException/NpgsqlException derive from DbException).</summary>
    private sealed class SimulatedDbException(string message) : System.Data.Common.DbException(message);

    [Fact]
    public async Task B3_07_MasterDbLookupFailure_AtIssuance_GenericResponse_NothingIssued()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var failing = new Mock<ITenantRepository>();
        failing.Setup(t => t.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SimulatedDbException("master database unavailable"));

        var result = await ServiceWithTenants(failing.Object)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(Generic, result.Value.Message);
        Assert.Null(result.Value.ResetToken);
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Fact]
    public async Task B3_07_MasterDbLookupFailure_AtRedemption_FailsClosed_PasswordUnchanged()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var token = await IssueAsync(tenant.Code);
        var failing = new Mock<ITenantRepository>();
        failing.Setup(t => t.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SimulatedDbException("master database unavailable"));

        await Assert.ThrowsAsync<SimulatedDbException>(() => ServiceWithTenants(failing.Object)
            .ResetPasswordAsync(new ResetPasswordRequest(Email, token!, NewPassword), tenant.Code));

        var stored = await ReloadUserAsync(user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, stored.PasswordHash));
        Assert.NotNull(stored.PasswordResetTokenHash);
    }

    [Fact]
    public async Task B3_07_TenantStatusReadFromRepository_NotFromAnyCache()
    {
        // The service resolves the tenant through ITenantRepository on every call; there is no
        // cache in that path. A repository that reports the tenant inactive wins, whatever
        // TenantMiddleware may have cached. (Stale-middleware-cache behaviour end to end is in
        // AuthController_PasswordRecoveryPipelineTests.)
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var inactive = new Tenant { Code = tenant.Code, Name = tenant.Name, ConnectionString = "x", DbProvider = "SqlServer", IsActive = false };
        var repo = new Mock<ITenantRepository>();
        repo.Setup(t => t.GetByCodeAsync(tenant.Code, It.IsAny<CancellationToken>())).ReturnsAsync(inactive);

        var result = await ServiceWithTenants(repo.Object)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        Assert.Null(result.Value.ResetToken);
        repo.Verify(t => t.GetByCodeAsync(tenant.Code, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    // ── B3-08: invalid, expired, previously redeemed tokens ──

    [Fact]
    public async Task B3_08_WrongToken_Rejected()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();
        await IssueAsync(tenant.Code);

        AssertGenericInvalid(await RedeemAsync(tenant.Code, "not-the-token"));
    }

    [Fact]
    public async Task B3_08_ExpiredToken_WithCorrectToken_ReportsExpired_AndClears()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var token = await IssueAsync(tenant.Code);
        var tracked = await Context.Set<AppUser>().SingleAsync(u => u.Id == user.Id);
        tracked.PasswordResetTokenExpiry = DateTime.UtcNow.AddMinutes(-1);
        await Context.SaveChangesAsync();

        var result = await RedeemAsync(tenant.Code, token!);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.ResetTokenExpired", result.Error.Code);
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Fact]
    public async Task B3_08_ExpiredToken_WithWrongToken_IsGenericInvalid_NotExpired_AndNotCleared()
    {
        // Expiry is only revealed to a holder of the correct token: a caller who merely knows the
        // email cannot learn that a token was issued (which would confirm account + membership).
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        await IssueAsync(tenant.Code);
        var tracked = await Context.Set<AppUser>().SingleAsync(u => u.Id == user.Id);
        tracked.PasswordResetTokenExpiry = DateTime.UtcNow.AddMinutes(-1);
        await Context.SaveChangesAsync();

        AssertGenericInvalid(await RedeemAsync(tenant.Code, "guessed-token"));
        Assert.NotNull((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Fact]
    public async Task B3_08_PreviouslyRedeemedToken_CannotBeReused()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();
        var token = await IssueAsync(tenant.Code);
        Assert.True((await RedeemAsync(tenant.Code, token!)).IsSuccess);

        AssertGenericInvalid(await RedeemAsync(tenant.Code, token!, newPassword: "Another@789"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task B3_08_EmptyToken_Rejected(string? token)
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();
        await IssueAsync(tenant.Code);

        AssertGenericInvalid(await RedeemAsync(tenant.Code, token!));
    }

    // ── B3-09: previously issued UNBOUND tokens are rejected (no legacy fallback) ──

    [Fact]
    public async Task B3_09_UnboundLegacyToken_StillUnexpired_IsRejected()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        const string legacyPlain = "legacy-token-issued-before-tenant-binding";
        var tracked = await Context.Set<AppUser>().SingleAsync(u => u.Id == user.Id);
        tracked.PasswordResetTokenHash = Sha256Hex(legacyPlain); // the pre-R2-DEV-02B-3A format
        tracked.PasswordResetTokenExpiry = DateTime.UtcNow.AddMinutes(10);
        await Context.SaveChangesAsync();

        AssertGenericInvalid(await RedeemAsync(tenant.Code, legacyPlain));
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, (await ReloadUserAsync(user.Id)).PasswordHash));
    }

    // ── B3-10: generic responses; global password; history; session revocation ──

    [Fact]
    public async Task B3_10_IssuanceRefusals_AreIndistinguishableFromEachOther()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var unrelated = await AddTenantAsync("XYZ");
        var inactive = await AddTenantAsync("OFF", active: false);
        await AddMembershipAsync(user, inactive);

        var responses = new[]
        {
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest("nobody@example.com"), tenant.Code, exposeToken: true), // unknown account
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), unrelated.Code, exposeToken: true),               // no membership
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), inactive.Code, exposeToken: true),                // inactive tenant
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), "no-such-tenant", exposeToken: true),             // unknown tenant
        };

        Assert.All(responses, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.Equal(new ForgotPasswordResponse(Generic), r.Value);
        });
        EmailServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task B3_10_ProductionResponses_IssuedAndRefused_AreIdentical()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();

        var issued = await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: false);
        var refused = await Sut.ForgotPasswordAsync(new ForgotPasswordRequest("nobody@example.com"), tenant.Code, exposeToken: false);

        Assert.Equal(refused.Value, issued.Value);
    }

    [Fact]
    public async Task B3_10_RedemptionRefusals_AreIndistinguishableFromEachOther()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var token = await IssueAsync(tenant.Code);
        var unrelated = await AddTenantAsync("XYZ");
        var inactive = await AddTenantAsync("OFF", active: false);

        var results = new[]
        {
            await RedeemAsync(tenant.Code, token!, email: "nobody@example.com"), // unknown account
            await RedeemAsync(unrelated.Code, token!),                           // not a member / wrong tenant
            await RedeemAsync(inactive.Code, token!),                            // inactive tenant
            await RedeemAsync("no-such-tenant", token!),                         // unknown tenant
            await RedeemAsync(tenant.Code, "wrong"),                             // wrong token
        };

        Assert.All(results, AssertGenericInvalid);
        Assert.True(BCrypt.Net.BCrypt.Verify(OldPassword, (await ReloadUserAsync(user.Id)).PasswordHash));
    }

    [Fact]
    public async Task B3_10_GlobalPassword_ResetThroughTenantB_AppliesToLoginThroughTenantA()
    {
        var (user, tenantA, _, _) = await SeedUserWithRoleAsync();
        var tenantB = await AddTenantAsync("BBB");
        await AddMembershipAsync(user, tenantB);
        var token = await IssueAsync(tenantB.Code);
        Assert.True((await RedeemAsync(tenantB.Code, token!)).IsSuccess);

        var oldLogin = await Sut.LoginAsync(new LoginRequest("testuser", OldPassword), tenantA.Code);
        var newLogin = await Sut.LoginAsync(new LoginRequest("testuser", NewPassword), tenantA.Code);

        Assert.True(oldLogin.IsFailure);
        Assert.True(newLogin.IsSuccess);
    }

    [Fact]
    public async Task B3_10_SuccessfulReset_RevokesAllSessions_RotatesStamp_ArchivesPassword()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var other = await AddTenantAsync("BBB");
        await Context.Set<UserSession>().AddRangeAsync(
            CreateTestSession(user.Id, tenant.Id, "r1", "jti-1"),
            CreateTestSession(user.Id, other.Id, "r2", "jti-2"));
        await Context.SaveChangesAsync();
        var stampBefore = user.SecurityStamp;
        var token = await IssueAsync(tenant.Code);

        Assert.True((await RedeemAsync(tenant.Code, token!)).IsSuccess);

        var sessions = await Context.Set<UserSession>().AsNoTracking().Where(s => s.UserId == user.Id).ToListAsync();
        Assert.All(sessions, s => { Assert.True(s.IsRevoked); Assert.Equal("PasswordReset", s.RevokedReason); });
        Assert.NotEqual(stampBefore, (await ReloadUserAsync(user.Id)).SecurityStamp);
        Assert.Single(await Context.Set<UserPasswordHistory>().AsNoTracking().Where(h => h.UserId == user.Id).ToListAsync());
        Assert.False(await IsJtiActiveAsync("jti-1")); // R2-DEV-03A: no JTI cache; the session check rejects it
        Assert.False(await IsJtiActiveAsync("jti-2")); // R2-DEV-03A: no JTI cache; the session check rejects it
    }

    [Fact]
    public async Task B3_10_PasswordHistory_StillEnforced()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();
        var token = await IssueAsync(tenant.Code);

        var result = await RedeemAsync(tenant.Code, token!, newPassword: OldPassword);

        Assert.True(result.IsFailure);
        Assert.Equal("Auth.PasswordReused", result.Error.Code);
    }

    // ── C1: persistence failures, ambiguous commits, email failures, logging ──
    //
    // Failure simulation is CONTROLLED (wrappers around the real MasterUnitOfWork); these tests
    // do not demonstrate real transactional rollback on SQL Server/PostgreSQL. "Before commit"
    // means the wrapper throws without saving; "ambiguous" means it saves, then throws — the
    // worst case of an exception whose commit actually happened.

    /// <summary>Throws a database failure WITHOUT saving (failure before commit).</summary>
    private sealed class FailBeforeCommitUnitOfWork : IMasterUnitOfWork
    {
        public int Calls { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new DbUpdateException("simulated failure before commit", new TimeoutException("simulated"));
        }
    }

    /// <summary>SAVES, then throws — an exception although the write was committed (ambiguous outcome).</summary>
    private sealed class CommitThenThrowUnitOfWork(IMasterUnitOfWork inner) : IMasterUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await inner.SaveChangesAsync(cancellationToken);
            throw new DbUpdateException("simulated: connection lost after commit", new TimeoutException("simulated"));
        }
    }

    private sealed record LogEntry(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception, string State);

    /// <summary>Captures every log call, including the structured state, so tests can inspect content.</summary>
    private sealed class CapturingLogger : ILogger<AuthService>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var stateText = state is IEnumerable<KeyValuePair<string, object?>> kv
                ? string.Join(";", kv.Select(p => $"{p.Key}={p.Value}"))
                : state?.ToString() ?? "";
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, stateText));
        }

        public IEnumerable<LogEntry> Errors => Entries.Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    private static void AssertNoSecretsLogged(CapturingLogger log, params string?[] secrets)
    {
        foreach (var entry in log.Entries)
            foreach (var secret in secrets.Where(s => !string.IsNullOrEmpty(s)))
            {
                Assert.DoesNotContain(secret!, entry.Message);
                Assert.DoesNotContain(secret!, entry.State);
            }
    }

    [Fact]
    public async Task C1_SaveFailsBeforeCommit_GenericResponse_NoToken_NoEmail_PreviousTokenStillValid()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var earlier = await IssueAsync(tenant.Code);
        var stored = await ReloadUserAsync(user.Id);
        var log = new CapturingLogger();
        var failing = new FailBeforeCommitUnitOfWork();

        var result = await BuildService(unitOfWork: failing, logger: log)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ForgotPasswordResponse(Generic), result.Value);
        Assert.Equal(1, failing.Calls);
        EmailServiceMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        var after = await ReloadUserAsync(user.Id);
        Assert.Equal(stored.PasswordResetTokenHash, after.PasswordResetTokenHash); // nothing was written

        // Nothing was written, so in the next request (fresh scope) the earlier token still works.
        // (The tracked entity in THIS scope is deliberately not reverted — see the real-EF test below.)
        Context.ChangeTracker.Clear(); // simulate the next request
        Assert.True((await RedeemAsync(tenant.Code, earlier!)).IsSuccess);

        var error = Assert.Single(log.Errors);
        Assert.Contains("commit outcome UNKNOWN", error.Message);
        Assert.Contains(user.Id.ToString(), error.Message);
        Assert.Matches("ForgotPassword [0-9a-f]{32} ", error.Message); // per-call operation id
        Assert.IsType<DbUpdateException>(error.Exception);
        AssertNoSecretsLogged(log, earlier, stored.PasswordResetTokenHash, Email, OldPassword);
    }

    [Fact]
    public async Task C1_AmbiguousCommit_TokenMayBeStored_ButIsNeverDelivered_AndGenericResponse()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var earlier = await IssueAsync(tenant.Code);
        var hashBefore = (await ReloadUserAsync(user.Id)).PasswordResetTokenHash;
        var log = new CapturingLogger();

        // exposeToken: true is the strongest check — even the dev/test path must not return a
        // token whose persistence outcome is unknown.
        var result = await BuildService(unitOfWork: new CommitThenThrowUnitOfWork(new MasterUnitOfWork(Context)), logger: log)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ForgotPasswordResponse(Generic), result.Value); // no token returned
        EmailServiceMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);

        // The write DID commit here: a new hash is stored. Skipping delivery did not remove it;
        // nobody holds its plaintext, so it cannot be redeemed, and it expires in 15 minutes.
        var stored = await ReloadUserAsync(user.Id);
        Assert.NotNull(stored.PasswordResetTokenHash);
        Assert.NotEqual(hashBefore, stored.PasswordResetTokenHash);

        // Side effect of the single token slot: the earlier outstanding token was replaced.
        Context.ChangeTracker.Clear();
        AssertGenericInvalid(await RedeemAsync(tenant.Code, earlier!));

        var error = Assert.Single(log.Errors);
        Assert.Contains("commit outcome UNKNOWN", error.Message);
        AssertNoSecretsLogged(log, earlier, hashBefore, stored.PasswordResetTokenHash, Email, OldPassword);
    }

    [Fact]
    public async Task C1_EmailFailureAfterSuccessfulSave_GenericResponse_TokenStored_ErrorLogged()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        string? sentToken = null;
        EmailServiceMock.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => sentToken = m.PlainTextBody)
            .ThrowsAsync(new InvalidOperationException("smtp unreachable"));
        var log = new CapturingLogger();

        var result = await BuildService(logger: log)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ForgotPasswordResponse(Generic), result.Value);
        Assert.NotNull((await ReloadUserAsync(user.Id)).PasswordResetTokenHash); // stored, undelivered

        var error = Assert.Single(log.Errors);
        Assert.Contains("email delivery FAILED", error.Message);
        Assert.Contains(user.Id.ToString(), error.Message);
        Assert.Equal("smtp unreachable", error.Exception?.Message);
        var plainToken = sentToken!.Split("token: ")[1].Split('\n')[0];
        AssertNoSecretsLogged(log, plainToken, (await ReloadUserAsync(user.Id)).PasswordResetTokenHash, Email, OldPassword);
    }

    [Fact]
    public async Task C1_SuccessfulIssuance_LogsNoTokenHashOrEmail()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var log = new CapturingLogger();

        var result = await BuildService(logger: log)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        var token = result.Value.ResetToken;
        Assert.NotNull(token);
        Assert.Empty(log.Errors);
        Assert.Contains(log.Entries, e => e.Message.Contains("PasswordResetToken issued") && e.Message.Contains(user.Id.ToString()));
        AssertNoSecretsLogged(log, token, (await ReloadUserAsync(user.Id)).PasswordResetTokenHash, Email, OldPassword);
    }

    [Fact]
    public async Task C1_NonDatabaseException_IsNotMasked_AndIssuesNothing()
    {
        // Only recognisable database failures are absorbed into the generic response. A bug
        // (any other exception type) still surfaces, so it cannot hide behind a 200.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var buggy = new Mock<ITenantRepository>();
        buggy.Setup(t => t.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("bug"));

        await Assert.ThrowsAsync<ArgumentException>(() => ServiceWithTenants(buggy.Object)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true));
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Theory]
    [InlineData("db-exception")]
    [InlineData("timeout")]
    [InlineData("ef-wrapped")]
    public async Task C1_MembershipLookupFailure_GenericResponse_NothingIssued(string kind)
    {
        // Membership is checked only for an existing, active user — so a failure here must not
        // produce an account-dependent error.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        Exception failure = kind switch
        {
            "db-exception" => new SimulatedDbException("down"),
            "timeout" => new TimeoutException("slow"),
            _ => new InvalidOperationException("transient failure", new SimulatedDbException("down")),
        };
        var users = new Mock<IUserRepository>();
        var realUsers = new UserRepository(Context);
        users.Setup(u => u.GetByNormalizedEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((e, c) => realUsers.GetByNormalizedEmailAsync(e, c));
        users.Setup(u => u.IsAssignedToTenantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var service = new AuthService(users.Object, new UserSessionRepository(Context), new UserPasswordHistoryRepository(Context),
            new TenantRepository(Context), new MasterUnitOfWork(Context), TokenServiceMock.Object, TestRefreshTokenHasher,
            EmailServiceMock.Object, Options.Create(TestJwtSettings), Options.Create(TestRefreshTokenSettings),NullLogger<AuthService>.Instance);

        var result = await service.ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        Assert.Equal(new ForgotPasswordResponse(Generic), result.Value);
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    // ── C1 round 3: provider-origin classification (L5), real-EF tracker state (I1), namespace-only match ──

    /// <summary>
    /// A REAL InvalidOperationException raised inside Microsoft.Data.SqlClient (no server needed:
    /// executing a command on a never-opened connection). Same type and origin as SqlClient's
    /// pool-exhaustion error, which cannot be reproduced without a SQL Server.
    /// </summary>
    private static InvalidOperationException RealSqlClientInvalidOperation()
    {
        try
        {
            using var connection = new Microsoft.Data.SqlClient.SqlConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.ExecuteScalar();
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        throw new Xunit.Sdk.XunitException("SqlClient did not throw InvalidOperationException");
    }

    [Fact]
    public void L5_SqlClientInvalidOperation_HasProviderSource_AndNoInnerException()
    {
        // Pins the premise of the classification: SqlClient's plain InvalidOperationException
        // carries no inner exception and reports the provider assembly as its Source.
        var ex = RealSqlClientInvalidOperation();

        Assert.Null(ex.InnerException);
        Assert.Equal(typeof(InvalidOperationException), ex.GetType());
        Assert.StartsWith("Microsoft.Data.SqlClient", ex.Source);
    }

    [Fact]
    public async Task L5_SqlClientInvalidOperation_AtMembershipCheck_GenericResponse_NothingIssued()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var users = new Mock<IUserRepository>();
        var realUsers = new UserRepository(Context);
        users.Setup(u => u.GetByNormalizedEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((e, c) => realUsers.GetByNormalizedEmailAsync(e, c));
        users.Setup(u => u.IsAssignedToTenantAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(RealSqlClientInvalidOperation());
        var service = new AuthService(users.Object, new UserSessionRepository(Context), new UserPasswordHistoryRepository(Context),
            new TenantRepository(Context), new MasterUnitOfWork(Context), TokenServiceMock.Object, TestRefreshTokenHasher,
            EmailServiceMock.Object, Options.Create(TestJwtSettings), Options.Create(TestRefreshTokenSettings),NullLogger<AuthService>.Instance);

        var result = await service.ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        Assert.Equal(new ForgotPasswordResponse(Generic), result.Value);
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Fact]
    public async Task L5_SqlClientInvalidOperation_AtSave_GenericResponse_NoTokenNoEmail()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();
        var uow = new Mock<IMasterUnitOfWork>();
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(RealSqlClientInvalidOperation());
        var log = new CapturingLogger();

        var result = await BuildService(unitOfWork: uow.Object, logger: log)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: false);

        Assert.Equal(new ForgotPasswordResponse(Generic), result.Value);
        EmailServiceMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("commit outcome UNKNOWN", Assert.Single(log.Errors).Message);
    }

    [Fact]
    public async Task L5_ApplicationInvalidOperation_IsStillNotMasked()
    {
        // The provider-origin rule must not swallow every InvalidOperationException: one raised
        // by application code (Source = the test assembly here) still surfaces.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var buggy = new Mock<ITenantRepository>();
        buggy.Setup(t => t.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("application bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceWithTenants(buggy.Object)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true));
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Fact]
    public async Task T5_DbUpdateExceptionWithoutInner_IsClassifiedByNamespaceAlone()
    {
        var (_, tenant, _, _) = await SeedUserWithRoleAsync();
        var uow = new Mock<IMasterUnitOfWork>();
        uow.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new DbUpdateException("no inner exception"));

        var result = await BuildService(unitOfWork: uow.Object)
            .ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        Assert.Equal(new ForgotPasswordResponse(Generic), result.Value);
    }

    /// <summary>Throws from INSIDE a real EF Core SaveChanges once armed (real change tracker).</summary>
    private sealed class FailingSaveInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                throw new DbUpdateException("simulated failure before commit (real EF pipeline)", new TimeoutException("simulated"));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task I1_RealEfSaveFailure_TrackerKeepsNewHash_LaterSaveCannotReviveEarlierToken()
    {
        // A real EF Core context (InMemory provider) whose SaveChanges fails before commit via an
        // interceptor, so the REAL change tracker state after the failure is observed. This shows
        // the reason the revert was removed: after the failure the new hash stays Modified, and a
        // later save in the same scope writes the NEW, undelivered hash — never the old one.
        var failing = new FailingSaveInterceptor();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());
        var dbName = Guid.NewGuid().ToString();
        DbContextOptions<MasterDbContext> Options_(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] i) =>
            new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(dbName).AddInterceptors(i).Options;
        await using var ctx = new MasterDbContext(Options_(new AuditableEntityInterceptor(currentUser.Object), failing));

        var user = CreateTestUser();
        var tenant = CreateTestTenant("EFX");
        await ctx.Set<AppUser>().AddAsync(user);
        await ctx.Set<Tenant>().AddAsync(tenant);
        await ctx.SaveChangesAsync();
        await ctx.Set<UserTenant>().AddAsync(new UserTenant { UserId = user.Id, TenantId = tenant.Id, IsActive = true });
        await ctx.SaveChangesAsync();

        var service = new AuthService(new UserRepository(ctx), new UserSessionRepository(ctx), new UserPasswordHistoryRepository(ctx),
            new TenantRepository(ctx), new MasterUnitOfWork(ctx), TokenServiceMock.Object, TestRefreshTokenHasher,
            EmailServiceMock.Object, Options.Create(TestJwtSettings), Options.Create(TestRefreshTokenSettings),NullLogger<AuthService>.Instance);

        var earlier = (await service.ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true)).Value.ResetToken;
        Assert.NotNull(earlier);

        async Task<string?> StoredHashAsync()
        {
            await using var reader = new MasterDbContext(Options_(new AuditableEntityInterceptor(currentUser.Object)));
            return await reader.Set<AppUser>().AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.PasswordResetTokenHash).SingleAsync();
        }
        var earlierHash = await StoredHashAsync();

        failing.Armed = true;
        var refused = await service.ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        Assert.Equal(new ForgotPasswordResponse(Generic), refused.Value);
        Assert.Equal(earlierHash, await StoredHashAsync());                 // nothing written
        var entry = ctx.Entry(user);
        Assert.True(entry.Property(u => u.PasswordResetTokenHash).IsModified); // real EF: still Modified
        Assert.NotEqual(earlierHash, user.PasswordResetTokenHash);          // holds the NEW, undelivered hash

        // Worst case: something later in the same scope saves. It writes the undelivered hash,
        // which nobody holds — it cannot revive or extend the earlier token.
        await ctx.SaveChangesAsync();
        var afterLaterSave = await StoredHashAsync();
        Assert.NotEqual(earlierHash, afterLaterSave);
        Assert.Equal(user.PasswordResetTokenHash, afterLaterSave);
    }

    [Fact]
    public async Task C1_InactiveUser_NoToken_GenericResponse()
    {
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        user.IsActive = false;
        await Context.SaveChangesAsync();

        var result = await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: true);

        Assert.Equal(new ForgotPasswordResponse(Generic), result.Value);
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);
    }

    [Fact]
    public async Task C1_RejectedRequests_InProduction_SendNoEmail_AndMatchTheIssuedResponse()
    {
        // exposeToken: false is the Production path — the only path that sends email.
        var (user, tenant, _, _) = await SeedUserWithRoleAsync();
        var unrelated = await AddTenantAsync("XYZ");
        var inactiveTenant = await AddTenantAsync("OFF", active: false);
        await AddMembershipAsync(user, inactiveTenant);
        var lapsed = await AddTenantAsync("LAP");
        await AddMembershipAsync(user, lapsed, active: false);

        var refused = new[]
        {
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest("nobody@example.com"), tenant.Code, exposeToken: false),
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), unrelated.Code, exposeToken: false),
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), inactiveTenant.Code, exposeToken: false),
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), lapsed.Code, exposeToken: false),
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), "no-such-tenant", exposeToken: false),
            await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), "", exposeToken: false),
        };
        EmailServiceMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Null((await ReloadUserAsync(user.Id)).PasswordResetTokenHash);

        var issued = await Sut.ForgotPasswordAsync(new ForgotPasswordRequest(Email), tenant.Code, exposeToken: false);
        EmailServiceMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);

        Assert.All(refused, r => Assert.Equal(issued.Value, r.Value));
    }

    // ── Known limitation, pinned: one token per user (global) ──

    [Fact]
    public async Task Limitation_SingleGlobalToken_IssuingThroughTenantB_ReplacesTokenFromTenantA()
    {
        // The user has one hash/expiry pair. A later request through another member tenant
        // overwrites an outstanding token. Changing that needs a schema change (out of scope).
        var (user, tenantA, _, _) = await SeedUserWithRoleAsync();
        var tenantB = await AddTenantAsync("BBB");
        await AddMembershipAsync(user, tenantB);
        var tokenA = await IssueAsync(tenantA.Code);
        var tokenB = await IssueAsync(tenantB.Code);

        AssertGenericInvalid(await RedeemAsync(tenantA.Code, tokenA!));
        Assert.True((await RedeemAsync(tenantB.Code, tokenB!)).IsSuccess);
    }
}
