using System.Diagnostics;
using System.Runtime.CompilerServices;
using ERP.Application.Abstractions.Email;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Identity.Abstractions;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Configuration;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Services;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using ERP.Persistence.PostgreSql.Extensions;
using ERP.Persistence.SqlServer.Extensions;
using ERP.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;

namespace ERP.UnitTests.Identity;

// ═════════════════════════════════════════════════════════════════════════════
// R2-DEV-03A — REAL-PROVIDER verification (SQL Server and PostgreSQL).
//
// These run ONLY when a server-level connection string is supplied:
//   ERP_03A_SQLSERVER   e.g. a disposable local container
//   ERP_03A_POSTGRESQL  e.g. a disposable local container
// Otherwise every test reports SKIPPED (never "passed"). The values are never printed.
//
// Safety: each class creates its OWN database named erp_03a_tmp_<guid> by applying every Master
// migration (including AddVersionedDeviceSessions), and drops only that database afterwards.
// Concurrency uses separate DbContexts and a two-party Barrier held right before SaveChanges, so
// both requests have passed every check and race on the same row.
// ═════════════════════════════════════════════════════════════════════════════

public enum RealProvider { SqlServer, PostgreSql }

/// <summary>Skips the test unless the provider's endpoint variable is set.</summary>
public sealed class RealProviderFactAttribute : FactAttribute
{
    public RealProviderFactAttribute(RealProvider provider)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RealProviderHarness.VariableFor(provider))))
            Skip = $"{RealProviderHarness.VariableFor(provider)} not set — real {provider} verification not run.";
    }
}

/// <summary>A disposable Master database on a real provider, plus factories for services over it.</summary>
public sealed class RealProviderHarness : IAsyncDisposable
{
    public static string VariableFor(RealProvider p) => p == RealProvider.SqlServer ? "ERP_03A_SQLSERVER" : "ERP_03A_POSTGRESQL";

    public RealProvider Provider { get; }
    public string DatabaseName { get; }
    private readonly string _connectionString;
    private readonly ICurrentUserService _currentUser;

    public RefreshTokenSettings RefreshSettings { get; } = new()
    {
        HmacKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
        DuplicateWindowSeconds = 10,
    };

    public JwtSettings JwtSettings { get; } = new()
    {
        SecretKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
        Issuer = "erp-03a-tests", Audience = "erp-03a-tests",
        AccessTokenExpirationMinutes = 60, RefreshTokenExpirationDays = 7,
    };

    private RealProviderHarness(RealProvider provider, string serverConnectionString)
    {
        RuntimeHelpers.RunClassConstructor(typeof(AuthServiceTestBase).TypeHandle); // Identity entity configs, once
        Provider = provider;
        DatabaseName = "erp_03a_tmp_" + Guid.NewGuid().ToString("N")[..20];
        _connectionString = provider == RealProvider.SqlServer
            ? new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = DatabaseName }.ConnectionString
            : new Npgsql.NpgsqlConnectionStringBuilder(serverConnectionString) { Database = DatabaseName }.ConnectionString;
        var cu = new Mock<ICurrentUserService>();
        cu.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());
        _currentUser = cu.Object;
    }

    /// <summary>Creates the disposable database. <paramref name="toMigration"/> null = all migrations.</summary>
    public static async Task<RealProviderHarness> CreateAsync(RealProvider provider, string? toMigration = null)
    {
        var server = Environment.GetEnvironmentVariable(VariableFor(provider))
            ?? throw new InvalidOperationException($"{VariableFor(provider)} not set");
        var harness = new RealProviderHarness(provider, server);
        await using var ctx = harness.NewContext();
        await ctx.GetService<IMigrator>().MigrateAsync(toMigration);
        return harness;
    }

    public MasterDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<MasterDbContext>();
        if (Provider == RealProvider.SqlServer) builder.UseSqlServerProvider(_connectionString);
        else builder.UsePostgreSqlProvider(_connectionString);
        builder.AddInterceptors(new AuditableEntityInterceptor(_currentUser));
        return new MasterDbContext((DbContextOptions<MasterDbContext>)builder.Options);
    }

    public TokenService NewTokenService() => new(Options.Create(JwtSettings));

    public IRefreshTokenHasher NewHasher() => new RefreshTokenHasher(Options.Create(RefreshSettings));

    /// <summary>One "request": an AuthService over its own context (optionally a wrapped unit of work).</summary>
    public (AuthService Service, MasterDbContext Context) NewRequest(Func<IMasterUnitOfWork, IMasterUnitOfWork>? wrap = null, IRefreshTokenHasher? hasher = null)
    {
        var ctx = NewContext();
        IMasterUnitOfWork uow = new MasterUnitOfWork(ctx);
        if (wrap is not null) uow = wrap(uow);
        var service = new AuthService(new UserRepository(ctx), new UserSessionRepository(ctx),
            new UserPasswordHistoryRepository(ctx), new TenantRepository(ctx), uow, NewTokenService(),
            hasher ?? NewHasher(), new Mock<IEmailService>().Object, Options.Create(JwtSettings),
            Options.Create(RefreshSettings), NullLogger<AuthService>.Instance);
        return (service, ctx);
    }

    /// <summary>Seeds an active tenant, an active user and membership; returns the tenant code.</summary>
    public async Task<(AppUser User, Tenant Tenant)> SeedUserAsync(string? tenantCode = null, AppUser? existingUser = null)
    {
        await using var ctx = NewContext();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenant = new Tenant
        {
            Code = TenantCode.Normalize(tenantCode ?? "t" + suffix), Name = "T " + suffix, ConnectionString = "unused",
            DbProvider = "SqlServer", IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        var user = existingUser ?? new AppUser
        {
            UserName = "u" + suffix, NormalizedUserName = ("u" + suffix).ToUpperInvariant(),
            Email = $"u{suffix}@example.com", NormalizedEmail = $"U{suffix}@EXAMPLE.COM".ToUpperInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4),
            FirstName = "R", LastName = "P", IsActive = true, SecurityStamp = Guid.NewGuid().ToString(),
        };
        ctx.Set<Tenant>().Add(tenant);
        if (existingUser is null) ctx.Set<AppUser>().Add(user);
        await ctx.SaveChangesAsync();
        ctx.Set<UserTenant>().Add(new UserTenant { UserId = user.Id, TenantId = tenant.Id, IsActive = true });
        await ctx.SaveChangesAsync();
        return (user, tenant);
    }

    public const string Password = "Password@123";

    public async Task<LoginResponse> LoginAsync(AppUser user, Tenant tenant)
    {
        var (svc, ctx) = NewRequest();
        await using (ctx)
        {
            var login = await svc.LoginAsync(new LoginRequest(user.UserName, Password), tenant.Code);
            Assert.True(login.IsSuccess, login.IsFailure ? login.Error.Code : null);
            return login.Value;
        }
    }

    public async Task<UserSession> SessionByRefreshAsync(string refreshToken)
    {
        await using var ctx = NewContext();
        var hash = NewHasher().Hash(refreshToken);
        return await ctx.Set<UserSession>().AsNoTracking().SingleAsync(s => s.RefreshTokenHash == hash);
    }

    public async Task<UserSession> SessionAsync(Guid id)
    {
        await using var ctx = NewContext();
        return await ctx.Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public async Task<bool> JtiActiveAsync(Guid userId, string jti)
    {
        await using var ctx = NewContext();
        return await new UserSessionRepository(ctx).IsAccessTokenSessionActiveAsync(userId, jti, DateTime.UtcNow);
    }

    public static string JtiOf(string accessToken) =>
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(accessToken).Id;

    public async Task AgeConsumptionAsync(string consumedRefresh, TimeSpan age)
    {
        await using var ctx = NewContext();
        var hash = NewHasher().Hash(consumedRefresh);
        var row = await ctx.Set<ConsumedRefreshToken>().SingleAsync(c => c.TokenHash == hash);
        row.ConsumedAt = DateTime.UtcNow - age;
        await ctx.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Provider == RealProvider.SqlServer) Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        else Npgsql.NpgsqlConnection.ClearAllPools();
        await using var ctx = NewContext();
        await ctx.Database.EnsureDeletedAsync(); // only the erp_03a_tmp_* database this harness created
    }
}

/// <summary>Holds the FIRST SaveChanges of a request at a barrier so two requests save together.</summary>
public sealed class BarrierUnitOfWork(IMasterUnitOfWork inner, Barrier barrier) : IMasterUnitOfWork
{
    private int _armed = 1;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            await Task.Run(() =>
            {
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("barrier not reached by the other request");
            }, cancellationToken);
        }

        return await inner.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Holds the FIRST SaveChanges until released, after signalling that it has arrived — used to
/// force a strict order: this request loads its state, another request commits, then this saves.
/// </summary>
public sealed class GateUnitOfWork(IMasterUnitOfWork inner, TaskCompletionSource arrived, Task release) : IMasterUnitOfWork
{
    private int _armed = 1;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            arrived.TrySetResult();
            await release.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        return await inner.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Saves for real, then throws: the commit happened but the caller sees a failure.</summary>
public sealed class CommitThenThrowUnitOfWork(IMasterUnitOfWork inner) : IMasterUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await inner.SaveChangesAsync(cancellationToken);
        throw new DbUpdateException("simulated: connection lost after commit", new TimeoutException("simulated"));
    }
}

/// <summary>Throws before anything is sent to the database.</summary>
public sealed class FailBeforeCommitUnitOfWork : IMasterUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new DbUpdateException("simulated failure before commit", new TimeoutException("simulated"));
}

/// <summary>Scenarios shared by both providers. Each scenario seeds its own tenant and user.</summary>
public abstract class RealProviderRefreshScenarios(RealProvider provider, ITestOutputHelper output) : IAsyncLifetime
{
    protected RealProviderHarness H = null!;
    protected readonly ITestOutputHelper Output = output;

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RealProviderHarness.VariableFor(provider))))
            H = await RealProviderHarness.CreateAsync(provider);
    }

    public async Task DisposeAsync()
    {
        if (H is not null) await H.DisposeAsync();
    }

    private static async Task<Result<LoginResponse>> RefreshAsync(AuthService svc, LoginResponse pair, Tenant tenant) =>
        await svc.RefreshTokenAsync(new RefreshTokenRequest(pair.AccessToken, pair.RefreshToken), tenant.Code);

    // ── 03A-01/02/03 + 04: contention → exactly one consumption; atomic history ──

    protected async Task Scenario_ConcurrentRefresh_ExactlyOneWins(int rounds)
    {
        var (user, tenant) = await H.SeedUserAsync();
        for (var round = 1; round <= rounds; round++)
        {
            var pair = await H.LoginAsync(user, tenant);
            var session = await H.SessionByRefreshAsync(pair.RefreshToken);
            using var barrier = new Barrier(2);
            var (a, ctxA) = H.NewRequest(u => new BarrierUnitOfWork(u, barrier));
            var (b, ctxB) = H.NewRequest(u => new BarrierUnitOfWork(u, barrier));
            await using (ctxA) await using (ctxB)
            {
                var results = await Task.WhenAll(
                    Task.Run(() => RefreshAsync(a, pair, tenant)),
                    Task.Run(() => RefreshAsync(b, pair, tenant)));

                var winners = results.Where(r => r.IsSuccess).ToList();
                Assert.Single(winners);
                Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal("Auth.InvalidRefreshToken", r.Error.Code));

                var stored = await H.SessionAsync(session.Id);
                Assert.Equal(H.NewHasher().Hash(winners[0].Value.RefreshToken), stored.RefreshTokenHash); // winner intact
                Assert.Equal(H.JtiOfToken(winners[0].Value.AccessToken), stored.AccessTokenJti);
                Assert.Equal(1, stored.RotationVersion);
                Assert.False(stored.IsRevoked);

                await using var check = H.NewContext();
                var history = await check.Set<ConsumedRefreshToken>().AsNoTracking().Where(c => c.SessionId == session.Id).ToListAsync();
                var only = Assert.Single(history); // the loser's history insert was rolled back with it
                Assert.Equal(H.NewHasher().Hash(pair.RefreshToken), only.TokenHash);
                Assert.Equal(0, only.RotationVersion);
            }
        }

        Output.WriteLine($"{provider}: {rounds} contended rounds, exactly one rotation committed in each.");
    }

    protected async Task Scenario_RotationAndHistory_CommitOrRollBackTogether()
    {
        // Force the history INSERT to fail (its unique digest already exists) and prove the session
        // UPDATE in the same SaveChanges was rolled back with it.
        var (user, tenant) = await H.SeedUserAsync();
        var pair = await H.LoginAsync(user, tenant);
        var session = await H.SessionByRefreshAsync(pair.RefreshToken);
        await using (var ctx = H.NewContext())
        {
            ctx.Set<ConsumedRefreshToken>().Add(new ConsumedRefreshToken
            {
                SessionId = session.Id, UserId = user.Id, TokenHash = H.NewHasher().Hash(pair.RefreshToken),
                AccessTokenJti = "blocker", RotationVersion = 99, ConsumedAt = DateTime.UtcNow, RetainUntil = DateTime.UtcNow.AddDays(1),
            });
            await ctx.SaveChangesAsync();
        }

        var (svc, sctx) = H.NewRequest();
        await using (sctx)
        {
            var result = await RefreshAsync(svc, pair, tenant);
            Assert.True(result.IsFailure);
        }

        var after = await H.SessionAsync(session.Id);
        Assert.Equal(0, after.RotationVersion);                                       // UPDATE rolled back
        Assert.Equal(H.NewHasher().Hash(pair.RefreshToken), after.RefreshTokenHash);  // still the old token
        Assert.Equal(session.AccessTokenJti, after.AccessTokenJti);

        // Remove the blocker: the same (never consumed) token now rotates normally.
        await using (var ctx = H.NewContext())
        {
            await ctx.Set<ConsumedRefreshToken>().Where(c => c.AccessTokenJti == "blocker").ExecuteDeleteAsync();
        }
        var (svc2, sctx2) = H.NewRequest();
        await using (sctx2) Assert.True((await RefreshAsync(svc2, pair, tenant)).IsSuccess);
    }

    // ── 03A-05: refresh vs logout / password change — a revocation always wins ──

    protected async Task Scenario_RefreshVersusLogout_RevocationAlwaysWins(int rounds)
    {
        var (user, tenant) = await H.SeedUserAsync();
        int refreshWon = 0, refreshLost = 0;
        for (var round = 1; round <= rounds; round++)
        {
            var pair = await H.LoginAsync(user, tenant);
            var session = await H.SessionByRefreshAsync(pair.RefreshToken);
            using var barrier = new Barrier(2);
            var (r, ctxR) = H.NewRequest(u => new BarrierUnitOfWork(u, barrier));
            var (l, ctxL) = H.NewRequest(u => new BarrierUnitOfWork(u, barrier));
            await using (ctxR) await using (ctxL)
            {
                var refreshTask = Task.Run(() => RefreshAsync(r, pair, tenant));
                var logoutTask = Task.Run(() => l.LogoutAsync(user.Id, pair.RefreshToken));
                await Task.WhenAll(refreshTask, logoutTask);

                Assert.True(logoutTask.Result.IsSuccess);
                var final = await H.SessionAsync(session.Id);
                Assert.True(final.IsRevoked);              // never restored by the refresh
                Assert.Equal("Logout", final.RevokedReason);

                var refresh = refreshTask.Result;
                if (refresh.IsSuccess)
                {
                    refreshWon++;
                    // Rotation committed first; logout then re-read and revoked the NEW state.
                    Assert.False(await H.JtiActiveAsync(user.Id, H.JtiOfToken(refresh.Value.AccessToken)));
                    var (v, vctx) = H.NewRequest();
                    await using (vctx) Assert.True((await RefreshAsync(v, refresh.Value, tenant)).IsFailure);
                }
                else
                {
                    refreshLost++;
                }

                Assert.False(await H.JtiActiveAsync(user.Id, H.JtiOfToken(pair.AccessToken)));
            }
        }

        Output.WriteLine($"{provider}: refresh vs logout × {rounds}: refresh committed first {refreshWon}×, lost {refreshLost}×; session revoked every time.");
    }

    protected async Task Scenario_RefreshVersusPasswordChange_RevocationAlwaysWins(int rounds)
    {
        var (user, tenant) = await H.SeedUserAsync();
        var password = RealProviderHarness.Password;
        for (var round = 1; round <= rounds; round++)
        {
            var pair = await H.LoginAsync(user, tenant);
            var session = await H.SessionByRefreshAsync(pair.RefreshToken);
            var newPassword = $"Changed@{round:000}x";
            using var barrier = new Barrier(2);
            var (r, ctxR) = H.NewRequest(u => new BarrierUnitOfWork(u, barrier));
            var (p, ctxP) = H.NewRequest(u => new BarrierUnitOfWork(u, barrier));
            await using (ctxR) await using (ctxP)
            {
                var refreshTask = Task.Run(() => RefreshAsync(r, pair, tenant));
                var changeTask = Task.Run(() => p.ChangePasswordAsync(user.Id, password, newPassword));
                await Task.WhenAll(refreshTask, changeTask);

                Assert.True(changeTask.Result.IsSuccess, changeTask.Result.IsFailure ? changeTask.Result.Error.Code : null);
                var final = await H.SessionAsync(session.Id);
                Assert.True(final.IsRevoked);
                Assert.Equal("PasswordChanged", final.RevokedReason);
                if (refreshTask.Result.IsSuccess)
                    Assert.False(await H.JtiActiveAsync(user.Id, H.JtiOfToken(refreshTask.Result.Value.AccessToken)));
            }

            // Next round logs in with the new password; keep the harness password in sync.
            await using (var ctx = H.NewContext())
            {
                var u = await ctx.Set<AppUser>().SingleAsync(x => x.Id == user.Id);
                Assert.True(BCrypt.Net.BCrypt.Verify(newPassword, u.PasswordHash));
                u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 4);
                await ctx.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// DETERMINISTIC: the revoker (logout or password change) has loaded the session and is held
    /// at SaveChanges; a refresh then COMMITS a rotation; only then is the revoker released. Its
    /// write is stale, must conflict, re-read and still revoke — and the rotation's new pair must
    /// be dead afterwards. This is the branch an unordered race rarely produces.
    /// </summary>
    protected async Task Scenario_StaleRevocation_AfterCommittedRotation_ReloadsAndWins(bool passwordChange)
    {
        var (user, tenant) = await H.SeedUserAsync();
        var pair = await H.LoginAsync(user, tenant);
        var session = await H.SessionByRefreshAsync(pair.RefreshToken);

        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (revoker, ctxRevoker) = H.NewRequest(u => new GateUnitOfWork(u, arrived, release.Task));
        await using (ctxRevoker)
        {
            var revokeTask = passwordChange
                ? Task.Run(() => revoker.ChangePasswordAsync(user.Id, RealProviderHarness.Password, "Changed@Stale1x"))
                : Task.Run(() => revoker.LogoutAsync(user.Id, pair.RefreshToken));
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30)); // revoker holds a version-0 copy

            var (refresher, ctxRefresh) = H.NewRequest();
            Result<LoginResponse> refresh;
            await using (ctxRefresh) refresh = await RefreshAsync(refresher, pair, tenant);
            Assert.True(refresh.IsSuccess);                              // rotation committed first
            Assert.Equal(1, (await H.SessionAsync(session.Id)).RotationVersion);

            release.SetResult();
            var revoked = await revokeTask;
            Assert.True(revoked.IsSuccess, revoked.IsFailure ? revoked.Error.Code : null);

            var final = await H.SessionAsync(session.Id);
            Assert.True(final.IsRevoked);
            Assert.Equal(passwordChange ? "PasswordChanged" : "Logout", final.RevokedReason);
            Assert.Equal(2, final.RotationVersion);                      // re-read (1) then revoked (+1)
            Assert.False(await H.JtiActiveAsync(user.Id, H.JtiOfToken(refresh.Value.AccessToken)));
            var (v, vctx) = H.NewRequest();
            await using (vctx) Assert.True((await RefreshAsync(v, refresh.Value, tenant)).IsFailure);

            if (passwordChange)
            {
                // The password change itself committed exactly once despite the retry.
                await using var check = H.NewContext();
                Assert.Single(await check.Set<UserPasswordHistory>().AsNoTracking().Where(x => x.UserId == user.Id).ToListAsync());
                var u = await check.Set<AppUser>().AsNoTracking().SingleAsync(x => x.Id == user.Id);
                Assert.True(BCrypt.Net.BCrypt.Verify("Changed@Stale1x", u.PasswordHash));
            }
        }
    }

    // ── 03A-06/07/08/10: duplicate, reuse, unknown token, device & tenant independence ──

    protected async Task Scenario_ReplayClassification_AndIndependence()
    {
        var (user, tenantA) = await H.SeedUserAsync();
        var (_, tenantB) = await H.SeedUserAsync(existingUser: user);
        var family = await H.LoginAsync(user, tenantA);
        var otherDevice = await H.LoginAsync(user, tenantA);
        var otherTenant = await H.LoginAsync(user, tenantB);
        var familySession = await H.SessionByRefreshAsync(family.RefreshToken);

        LoginResponse current;
        var (s1, c1) = H.NewRequest();
        await using (c1)
        {
            var rotated = await RefreshAsync(s1, family, tenantA);
            Assert.True(rotated.IsSuccess);
            current = rotated.Value;
        }

        // Duplicate inside the window: rejected, family NOT revoked, current pair still valid.
        var (s2, c2) = H.NewRequest();
        await using (c2) Assert.True((await RefreshAsync(s2, family, tenantA)).IsFailure);
        Assert.False((await H.SessionAsync(familySession.Id)).IsRevoked);
        Assert.True(await H.JtiActiveAsync(user.Id, H.JtiOfToken(current.AccessToken)));

        // Arbitrary unknown token: rejected, nothing revoked.
        var forged = family with { RefreshToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)) };
        var (s3, c3) = H.NewRequest();
        await using (c3) Assert.True((await RefreshAsync(s3, forged, tenantA)).IsFailure);
        Assert.False((await H.SessionAsync(familySession.Id)).IsRevoked);

        // Consumed digest with the WRONG access token (another device's): no revocation.
        await H.AgeConsumptionAsync(family.RefreshToken, TimeSpan.FromSeconds(H.RefreshSettings.DuplicateWindowSeconds + 5));
        var mismatched = family with { AccessToken = otherDevice.AccessToken };
        var (s4, c4) = H.NewRequest();
        await using (c4) Assert.True((await RefreshAsync(s4, mismatched, tenantA)).IsFailure);
        Assert.False((await H.SessionAsync(familySession.Id)).IsRevoked);

        // Reuse of the consumed pair after the window: ONLY this family is revoked.
        var (s5, c5) = H.NewRequest();
        await using (c5) Assert.True((await RefreshAsync(s5, family, tenantA)).IsFailure);
        var revoked = await H.SessionAsync(familySession.Id);
        Assert.True(revoked.IsRevoked);
        Assert.Equal("RefreshTokenReuse", revoked.RevokedReason);
        Assert.False(await H.JtiActiveAsync(user.Id, H.JtiOfToken(current.AccessToken)));

        // Other device and other tenant sessions are untouched and still rotate.
        var (s6, c6) = H.NewRequest();
        await using (c6) Assert.True((await RefreshAsync(s6, otherDevice, tenantA)).IsSuccess);
        var (s7, c7) = H.NewRequest();
        await using (c7) Assert.True((await RefreshAsync(s7, otherTenant, tenantB)).IsSuccess);
    }

    // ── 03A-09: detection survives an application restart ──

    protected async Task Scenario_ReuseDetectedAfterRestart()
    {
        var (user, tenant) = await H.SeedUserAsync();
        var pair = await H.LoginAsync(user, tenant);
        var session = await H.SessionByRefreshAsync(pair.RefreshToken);
        var (s1, c1) = H.NewRequest();
        await using (c1) Assert.True((await RefreshAsync(s1, pair, tenant)).IsSuccess);
        await H.AgeConsumptionAsync(pair.RefreshToken, TimeSpan.FromMinutes(5));

        // "Restart": brand-new hasher, token service, context and service — nothing in memory survives.
        // Only the durable history and the (stable, provisioned) key carry over.
        var (restarted, c2) = H.NewRequest(hasher: new RefreshTokenHasher(Options.Create(new RefreshTokenSettings
        {
            HmacKey = H.RefreshSettings.HmacKey, DuplicateWindowSeconds = H.RefreshSettings.DuplicateWindowSeconds,
        })));
        await using (c2) Assert.True((await RefreshAsync(restarted, pair, tenant)).IsFailure);

        var after = await H.SessionAsync(session.Id);
        Assert.True(after.IsRevoked);
        Assert.Equal("RefreshTokenReuse", after.RevokedReason);
    }

    // ── 03A-11: ambiguous commit and database failure ──

    protected async Task Scenario_AmbiguousCommit_IssuesNothing_AndOldPairClassifiesCorrectly()
    {
        var (user, tenant) = await H.SeedUserAsync();
        var pair = await H.LoginAsync(user, tenant);
        var session = await H.SessionByRefreshAsync(pair.RefreshToken);

        var (svc, ctx) = H.NewRequest(u => new CommitThenThrowUnitOfWork(u));
        await using (ctx)
        {
            var result = await RefreshAsync(svc, pair, tenant);
            Assert.True(result.IsFailure); // no credentials although the rotation committed
        }

        var committed = await H.SessionAsync(session.Id);
        Assert.Equal(1, committed.RotationVersion); // it DID commit — outcome was ambiguous to the caller
        Assert.False(committed.IsRevoked);

        // The client retries with the old pair: inside the window → duplicate, nothing revoked.
        var (s2, c2) = H.NewRequest();
        await using (c2) Assert.True((await RefreshAsync(s2, pair, tenant)).IsFailure);
        Assert.False((await H.SessionAsync(session.Id)).IsRevoked);

        // After the window the same pair is classified as reuse and the family is revoked.
        await H.AgeConsumptionAsync(pair.RefreshToken, TimeSpan.FromMinutes(1));
        var (s3, c3) = H.NewRequest();
        await using (c3) Assert.True((await RefreshAsync(s3, pair, tenant)).IsFailure);
        Assert.True((await H.SessionAsync(session.Id)).IsRevoked);
    }

    protected async Task Scenario_FailureBeforeCommit_LeavesOldPairValid()
    {
        var (user, tenant) = await H.SeedUserAsync();
        var pair = await H.LoginAsync(user, tenant);
        var session = await H.SessionByRefreshAsync(pair.RefreshToken);

        var (svc, ctx) = H.NewRequest(_ => new FailBeforeCommitUnitOfWork());
        await using (ctx) Assert.True((await RefreshAsync(svc, pair, tenant)).IsFailure);

        var untouched = await H.SessionAsync(session.Id);
        Assert.Equal(0, untouched.RotationVersion);
        await using (var check = H.NewContext())
            Assert.False(await check.Set<ConsumedRefreshToken>().AnyAsync(c => c.SessionId == session.Id));

        var (s2, c2) = H.NewRequest();
        await using (c2) Assert.True((await RefreshAsync(s2, pair, tenant)).IsSuccess); // retry works
    }

    // ── 03A-12: cross-instance access-token revocation + query overhead ──

    protected async Task Scenario_CrossInstanceRevocationVisibility_AndQueryCost(int samples)
    {
        var (user, tenant) = await H.SeedUserAsync();
        var pair = await H.LoginAsync(user, tenant);
        var jti = H.JtiOfToken(pair.AccessToken);

        // "Instance A" and "instance B": separate contexts (and connection-pool clients).
        await using var instanceA = H.NewContext();
        var repoA = new UserSessionRepository(instanceA);
        Assert.True(await repoA.IsAccessTokenSessionActiveAsync(user.Id, jti, DateTime.UtcNow));

        // Measure the per-request lookup that replaced the 2-minute cache (warm connection).
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < samples; i++)
            await repoA.IsAccessTokenSessionActiveAsync(user.Id, jti, DateTime.UtcNow);
        sw.Stop();
        Output.WriteLine($"{provider}: JTI validation query — {samples} lookups, {sw.Elapsed.TotalMilliseconds / samples:F3} ms average (local container, warm connection).");

        var (b, ctxB) = H.NewRequest();
        await using (ctxB) Assert.True((await b.LogoutAsync(user.Id, pair.RefreshToken)).IsSuccess);

        // The very next check on instance A sees the revocation — no cache to wait out.
        Assert.False(await repoA.IsAccessTokenSessionActiveAsync(user.Id, jti, DateTime.UtcNow));
    }

    // ── Retention: bounded purge never touches live-session history ──

    protected async Task Scenario_PurgeIsBounded_AndKeepsLiveHistory()
    {
        var (user, tenant) = await H.SeedUserAsync();
        var pair = await H.LoginAsync(user, tenant);
        var session = await H.SessionByRefreshAsync(pair.RefreshToken);
        var now = DateTime.UtcNow;
        await using (var ctx = H.NewContext())
        {
            for (var i = 0; i < 5; i++)
                ctx.Set<ConsumedRefreshToken>().Add(new ConsumedRefreshToken
                {
                    SessionId = session.Id, UserId = user.Id, TokenHash = $"expired-{i:00}-{Guid.NewGuid():N}",
                    AccessTokenJti = "x", RotationVersion = i, ConsumedAt = now.AddDays(-10), RetainUntil = now.AddDays(-1).AddMinutes(i),
                });
            ctx.Set<ConsumedRefreshToken>().Add(new ConsumedRefreshToken
            {
                SessionId = session.Id, UserId = user.Id, TokenHash = $"live-{Guid.NewGuid():N}",
                AccessTokenJti = "x", RotationVersion = 9, ConsumedAt = now, RetainUntil = session.ExpiresAt,
            });
            await ctx.SaveChangesAsync();
        }

        await using var maint = H.NewContext();
        var repo = new UserSessionRepository(maint);
        Assert.Equal(2, await repo.PurgeExpiredConsumedAsync(now, batchSize: 2, dryRun: true));
        Assert.Equal(2, await repo.PurgeExpiredConsumedAsync(now, batchSize: 2, dryRun: false)); // bounded
        Assert.Equal(3, await repo.PurgeExpiredConsumedAsync(now, batchSize: 100, dryRun: false));
        Assert.Equal(0, await repo.PurgeExpiredConsumedAsync(now, batchSize: 100, dryRun: false));

        await using var check = H.NewContext();
        var remaining = await check.Set<ConsumedRefreshToken>().AsNoTracking().Where(c => c.SessionId == session.Id).ToListAsync();
        Assert.Single(remaining);
        Assert.StartsWith("live-", remaining[0].TokenHash);
    }
}

public static class RealProviderHarnessExtensions
{
    public static string JtiOfToken(this RealProviderHarness _, string accessToken) => RealProviderHarness.JtiOf(accessToken);
}

/// <summary>Migration cutover / rollback on each provider, on its own disposable database.</summary>
public abstract class RealProviderMigrationScenarios(RealProvider provider, string previousMigration)
{
    protected async Task Scenario_Cutover_RevokesLegacySessions_RemovesPlaintext_AndRollbackWorks()
    {
        await using var h = await RealProviderHarness.CreateAsync(provider); // all migrations
        var (user, tenant) = await h.SeedUserAsync();
        var login = await h.LoginAsync(user, tenant); // a live 03A session
        var sessionId = (await h.SessionByRefreshAsync(login.RefreshToken)).Id;

        await using var ctx = h.NewContext();
        var migrator = ctx.GetService<IMigrator>();
        var sessions = provider == RealProvider.SqlServer ? "[UserSessions]" : "\"UserSessions\"";
        string Col(string c) => provider == RealProvider.SqlServer ? $"[{c}]" : $"\"{c}\"";
        string IdLiteral(Guid id) => provider == RealProvider.SqlServer ? $"'{id}'" : $"'{id}'::uuid";

        // ROLLBACK (Down): plaintext column comes back with unique, unusable values; all revoked.
        await migrator.MigrateAsync(previousMigration);
        Assert.True(await ColumnExistsAsync(ctx, "UserSessions", "RefreshToken"));
        Assert.False(await ColumnExistsAsync(ctx, "UserSessions", "RefreshTokenHash"));
        Assert.False(await TableExistsAsync(ctx, "ConsumedRefreshTokens"));
        Assert.Equal(1, await ScalarAsync(ctx, $"SELECT COUNT(*) FROM {sessions} WHERE {Col("Id")} = {IdLiteral(sessionId)} AND {Col("IsRevoked")} = {(provider == RealProvider.SqlServer ? "1" : "TRUE")} AND {Col("RefreshToken")} LIKE 'rolled-back-%'"));

        // Simulate a LEGACY (pre-03A) live session holding a plaintext refresh token. Built only from
        // constant identifiers and a Guid, run through the same scalar helper (no user input).
        var legacySql = $"UPDATE {sessions} SET {Col("IsRevoked")} = {(provider == RealProvider.SqlServer ? "0" : "FALSE")}, {Col("RevokedReason")} = NULL, {Col("RevokedAt")} = NULL, {Col("RefreshToken")} = 'legacy-plaintext-token' WHERE {Col("Id")} = {IdLiteral(sessionId)}";
        await ScalarAsync(ctx, legacySql + (provider == RealProvider.SqlServer ? "; SELECT @@ROWCOUNT" : " RETURNING 1"));

        // CUTOVER (Up): plaintext dropped, legacy session revoked with a non-matchable digest.
        await migrator.MigrateAsync();
        Assert.False(await ColumnExistsAsync(ctx, "UserSessions", "RefreshToken"));
        Assert.True(await TableExistsAsync(ctx, "ConsumedRefreshTokens"));
        await using var check = h.NewContext();
        var legacy = await check.Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.True(legacy.IsRevoked);
        Assert.Equal("Cutover-R2-DEV-03A", legacy.RevokedReason);
        Assert.StartsWith("legacy-", legacy.RefreshTokenHash);
        Assert.DoesNotMatch("^[0-9a-f]{64}$", legacy.RefreshTokenHash);

        // The legacy session can no longer be used at all.
        Assert.False(await h.JtiActiveAsync(user.Id, RealProviderHarness.JtiOf(login.AccessToken)));
        var (svc, sctx) = h.NewRequest();
        await using (sctx)
            Assert.True((await svc.RefreshTokenAsync(new RefreshTokenRequest(login.AccessToken, login.RefreshToken), tenant.Code)).IsFailure);
    }

    private async Task<bool> ColumnExistsAsync(MasterDbContext ctx, string table, string column) =>
        await ScalarAsync(ctx, provider == RealProvider.SqlServer
            ? $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'"
            : $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = '{column}'") == 1;

    private async Task<bool> TableExistsAsync(MasterDbContext ctx, string table) =>
        await ScalarAsync(ctx, provider == RealProvider.SqlServer
            ? $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = '{table}'"
            : $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'") == 1;

    private static async Task<long> ScalarAsync(MasterDbContext ctx, string sql)
    {
        var conn = ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}

// ── Concrete provider classes (separate databases; xUnit runs classes in parallel) ──

public sealed class RealProvider_SqlServer_RefreshConcurrencyTests(ITestOutputHelper output)
    : RealProviderRefreshScenarios(RealProvider.SqlServer, output)
{
    [RealProviderFact(RealProvider.SqlServer)] public Task ConcurrentRefresh_ExactlyOneWins() => Scenario_ConcurrentRefresh_ExactlyOneWins(rounds: 20);
    [RealProviderFact(RealProvider.SqlServer)] public Task RotationAndHistory_Atomic() => Scenario_RotationAndHistory_CommitOrRollBackTogether();
    [RealProviderFact(RealProvider.SqlServer)] public Task RefreshVersusLogout() => Scenario_RefreshVersusLogout_RevocationAlwaysWins(rounds: 20);
    [RealProviderFact(RealProvider.SqlServer)] public Task RefreshVersusPasswordChange() => Scenario_RefreshVersusPasswordChange_RevocationAlwaysWins(rounds: 5);
    [RealProviderFact(RealProvider.SqlServer)] public Task StaleLogout_AfterCommittedRotation() => Scenario_StaleRevocation_AfterCommittedRotation_ReloadsAndWins(passwordChange: false);
    [RealProviderFact(RealProvider.SqlServer)] public Task StalePasswordChange_AfterCommittedRotation() => Scenario_StaleRevocation_AfterCommittedRotation_ReloadsAndWins(passwordChange: true);
    [RealProviderFact(RealProvider.SqlServer)] public Task ReplayClassification_AndIndependence() => Scenario_ReplayClassification_AndIndependence();
    [RealProviderFact(RealProvider.SqlServer)] public Task ReuseDetectedAfterRestart() => Scenario_ReuseDetectedAfterRestart();
    [RealProviderFact(RealProvider.SqlServer)] public Task AmbiguousCommit() => Scenario_AmbiguousCommit_IssuesNothing_AndOldPairClassifiesCorrectly();
    [RealProviderFact(RealProvider.SqlServer)] public Task FailureBeforeCommit() => Scenario_FailureBeforeCommit_LeavesOldPairValid();
    [RealProviderFact(RealProvider.SqlServer)] public Task CrossInstanceRevocation_AndQueryCost() => Scenario_CrossInstanceRevocationVisibility_AndQueryCost(samples: 500);
    [RealProviderFact(RealProvider.SqlServer)] public Task PurgeIsBounded() => Scenario_PurgeIsBounded_AndKeepsLiveHistory();
}

public sealed class RealProvider_PostgreSql_RefreshConcurrencyTests(ITestOutputHelper output)
    : RealProviderRefreshScenarios(RealProvider.PostgreSql, output)
{
    [RealProviderFact(RealProvider.PostgreSql)] public Task ConcurrentRefresh_ExactlyOneWins() => Scenario_ConcurrentRefresh_ExactlyOneWins(rounds: 20);
    [RealProviderFact(RealProvider.PostgreSql)] public Task RotationAndHistory_Atomic() => Scenario_RotationAndHistory_CommitOrRollBackTogether();
    [RealProviderFact(RealProvider.PostgreSql)] public Task RefreshVersusLogout() => Scenario_RefreshVersusLogout_RevocationAlwaysWins(rounds: 20);
    [RealProviderFact(RealProvider.PostgreSql)] public Task RefreshVersusPasswordChange() => Scenario_RefreshVersusPasswordChange_RevocationAlwaysWins(rounds: 5);
    [RealProviderFact(RealProvider.PostgreSql)] public Task StaleLogout_AfterCommittedRotation() => Scenario_StaleRevocation_AfterCommittedRotation_ReloadsAndWins(passwordChange: false);
    [RealProviderFact(RealProvider.PostgreSql)] public Task StalePasswordChange_AfterCommittedRotation() => Scenario_StaleRevocation_AfterCommittedRotation_ReloadsAndWins(passwordChange: true);
    [RealProviderFact(RealProvider.PostgreSql)] public Task ReplayClassification_AndIndependence() => Scenario_ReplayClassification_AndIndependence();
    [RealProviderFact(RealProvider.PostgreSql)] public Task ReuseDetectedAfterRestart() => Scenario_ReuseDetectedAfterRestart();
    [RealProviderFact(RealProvider.PostgreSql)] public Task AmbiguousCommit() => Scenario_AmbiguousCommit_IssuesNothing_AndOldPairClassifiesCorrectly();
    [RealProviderFact(RealProvider.PostgreSql)] public Task FailureBeforeCommit() => Scenario_FailureBeforeCommit_LeavesOldPairValid();
    [RealProviderFact(RealProvider.PostgreSql)] public Task CrossInstanceRevocation_AndQueryCost() => Scenario_CrossInstanceRevocationVisibility_AndQueryCost(samples: 500);
    [RealProviderFact(RealProvider.PostgreSql)] public Task PurgeIsBounded() => Scenario_PurgeIsBounded_AndKeepsLiveHistory();
}

public sealed class RealProvider_SqlServer_MigrationTests()
    : RealProviderMigrationScenarios(RealProvider.SqlServer, "20260509162834_AddPasswordResetToken")
{
    [RealProviderFact(RealProvider.SqlServer)] public Task Cutover_And_Rollback() => Scenario_Cutover_RevokesLegacySessions_RemovesPlaintext_AndRollbackWorks();
}

public sealed class RealProvider_PostgreSql_MigrationTests()
    : RealProviderMigrationScenarios(RealProvider.PostgreSql, "20261007022731_InitialMasterBaseline")
{
    [RealProviderFact(RealProvider.PostgreSql)] public Task Cutover_And_Rollback() => Scenario_Cutover_RevokesLegacySessions_RemovesPlaintext_AndRollbackWorks();
}
