using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ERP.Api.Controllers.v1;
using ERP.Application.Abstractions.Email;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Abstractions;
using ERP.Identity.Abstractions.Persistence;
using ERP.Identity.Configuration;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Services;
using ERP.Infrastructure.Tenant;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Moq;
using TenantEntity = ERP.Identity.Entities.Tenant;

namespace ERP.UnitTests.Identity;

/// <summary>
/// R2-DEV-02B-3A — Tenant-bound password recovery through the HTTP PIPELINE.
///
/// In-process, no server: a real ApplicationBuilder running UseRouting → TenantMiddleware →
/// MVC endpoints, with the real AuthController (attribute routing, [FromBody] JSON binding,
/// [FromHeader(X-Tenant-Id)] binding, result mapping), the real AuthService and repositories,
/// the real MasterDbTenantStore and a real singleton MemoryCache, over EF Core InMemory.
///
/// NOT covered: Kestrel/TestServer, UseAuthentication/UseAuthorization (the routes are
/// [AllowAnonymous]), GlobalExceptionMiddleware, real SQL Server/PostgreSQL.
/// </summary>
public class AuthController_PasswordRecoveryPipelineTests : IDisposable
{
    private const string Email = "recover@example.com";
    private const string Generic = "If that email is registered, a reset link has been sent.";

    private readonly ServiceProvider _root;
    private readonly RequestDelegate _app;

    public AuthController_PasswordRecoveryPipelineTests() : this(Environments.Development) { }

    /// <summary>Controlled failure: every save throws a database failure without writing (C1).</summary>
    private sealed class FailingSaveUnitOfWork : IMasterUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("simulated save failure", new TimeoutException("simulated"));
    }

    private AuthController_PasswordRecoveryPipelineTests(string environmentName, bool failSaves = false)
    {
        RuntimeHelpers.RunClassConstructor(typeof(AuthServiceTestBase).TypeHandle); // Identity entity configs, once

        var dbName = Guid.NewGuid().ToString();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        env.SetupGet(e => e.ApplicationName).Returns("ERP.Api");
        env.SetupGet(e => e.ContentRootPath).Returns(AppContext.BaseDirectory);
        env.SetupGet(e => e.ContentRootFileProvider).Returns(new NullFileProvider());
        env.SetupGet(e => e.WebRootFileProvider).Returns(new NullFileProvider());

        var tokens = new Mock<ITokenService>();
        tokens.Setup(t => t.GenerateRefreshToken()).Returns("refresh");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton(new DiagnosticListener("ERP.UnitTests"));
        services.AddSingleton<DiagnosticSource>(sp => sp.GetRequiredService<DiagnosticListener>());
        services.AddSingleton(env.Object);
        services.AddSingleton<IHostEnvironment>(env.Object);
        services.AddRouting();
        services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);

        services.AddDbContext<MasterDbContext>(o => o
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new AuditableEntityInterceptor(currentUser.Object)));
        services.AddScoped<ITenantStore, MasterDbTenantStore>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserSessionRepository, UserSessionRepository>();
        services.AddScoped<IUserPasswordHistoryRepository, UserPasswordHistoryRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        if (failSaves)
            services.AddScoped<IMasterUnitOfWork, FailingSaveUnitOfWork>();
        else
            services.AddScoped<IMasterUnitOfWork, MasterUnitOfWork>();
        services.AddSingleton(tokens.Object);
        services.AddSingleton(new Mock<IEmailService>().Object);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new JwtSettings
        {
            SecretKey = "test-secret-key-32-chars-minimum!!", Issuer = "i", Audience = "a",
            AccessTokenExpirationMinutes = 60, RefreshTokenExpirationDays = 7
        }));
        // R2-DEV-03A: refresh-token digest key (random per run; never a committed value).
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new RefreshTokenSettings
        {
            HmacKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
        }));
        services.AddSingleton<IRefreshTokenHasher, RefreshTokenHasher>();
        services.AddScoped<IAuthService, AuthService>();

        _root = services.BuildServiceProvider();

        var app = new ApplicationBuilder(_root);
        app.UseRouting();
        app.UseMiddleware<TenantMiddleware>();
        app.UseEndpoints(e => e.MapControllers());
        _app = app.Build();
    }

    public void Dispose() => _root.Dispose();

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private sealed record Response(int Status, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    private async Task<Response> PostAsync(string path, object body, string? tenantHeader)
    {
        using var scope = _root.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (tenantHeader is not null)
            context.Request.Headers["X-Tenant-Id"] = tenantHeader;
        context.Response.Body = new MemoryStream();

        await _app(context);

        context.Response.Body.Position = 0;
        return new Response(context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private Task<Response> ForgotAsync(string? tenant, string email = Email) =>
        PostAsync("/api/v1/auth/forgot-password", new { email }, tenant);

    private Task<Response> ResetAsync(string? tenant, string token, string email = Email, string newPassword = "NewPassword@456") =>
        PostAsync("/api/v1/auth/reset-password", new { email, token, newPassword }, tenant);

    private async Task WithDbAsync(Func<MasterDbContext, Task> action)
    {
        using var scope = _root.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    private async Task<T> ReadDbAsync<T>(Func<MasterDbContext, Task<T>> query)
    {
        using var scope = _root.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<MasterDbContext>());
    }

    private static TenantEntity NewTenant(string code, bool active = true) => new()
    {
        Code = TenantCode.Normalize(code), Name = code, ConnectionString = "x", DbProvider = "SqlServer",
        IsActive = active, CreatedAt = DateTime.UtcNow,
    };

    /// <summary>Seeds a user who is an active member of every tenant code given.</summary>
    private async Task<(Guid UserId, Dictionary<string, TenantEntity> Tenants)> SeedAsync(
        string[] memberOf, string[]? alsoExisting = null)
    {
        var user = new AppUser
        {
            UserName = "recover", NormalizedUserName = "RECOVER", Email = Email, NormalizedEmail = Email.ToUpperInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password@123", workFactor: 4), FirstName = "R", LastName = "U",
            IsActive = true, SecurityStamp = Guid.NewGuid().ToString(),
        };
        var tenants = memberOf.Concat(alsoExisting ?? []).ToDictionary(c => c, c => NewTenant(c));
        await WithDbAsync(async db =>
        {
            await db.Set<AppUser>().AddAsync(user);
            await db.Set<TenantEntity>().AddRangeAsync(tenants.Values);
        });
        await WithDbAsync(async db =>
        {
            foreach (var code in memberOf)
                await db.Set<UserTenant>().AddAsync(new UserTenant { UserId = user.Id, TenantId = tenants[code].Id, IsActive = true });
        });
        return (user.Id, tenants);
    }

    private static string? TokenOf(Response r) =>
        r.Json.TryGetProperty("resetToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Http_IssueAndRedeem_ThroughSameTenant_Succeeds()
    {
        await SeedAsync(["tenant-a"]);

        var forgot = await ForgotAsync("tenant-a");
        Assert.Equal(200, forgot.Status);
        Assert.Equal(Generic, forgot.Json.GetProperty("message").GetString());
        var token = TokenOf(forgot);
        Assert.NotNull(token); // Development environment: token exposed for smoke testing

        var reset = await ResetAsync("tenant-a", token!);
        Assert.Equal(200, reset.Status);
        Assert.Contains("Password reset successfully", reset.Body);
    }

    [Fact]
    public async Task Http_TokenFromTenantA_RedeemedUnderTenantB_MemberOfBoth_Returns400Generic()
    {
        await SeedAsync(["tenant-a", "tenant-b"]);
        var token = TokenOf(await ForgotAsync("tenant-a"));

        var underB = await ResetAsync("tenant-b", token!);

        Assert.Equal(400, underB.Status);
        Assert.Equal("Invalid or expired password reset token.", underB.Json.GetProperty("error").GetString());
        Assert.Equal(200, (await ResetAsync("tenant-a", token!)).Status); // still valid where it was issued
    }

    [Fact]
    public async Task Http_StaleMiddlewareCache_TenantDeactivatedAfterCaching_NoTokenIssued()
    {
        var (userId, tenants) = await SeedAsync(["tenant-a"]);

        // 1. Anonymous request: TenantMiddleware caches tenant-a as ACTIVE (30-minute cache).
        Assert.NotNull(TokenOf(await ForgotAsync("tenant-a")));
        var hashBefore = await ReadDbAsync(db => db.Set<AppUser>().AsNoTracking().Where(u => u.Id == userId)
            .Select(u => u.PasswordResetTokenHash).SingleAsync());

        // 2. Tenant deactivated in the Master DB. The middleware cache still says active.
        await WithDbAsync(async db =>
            (await db.Set<TenantEntity>().SingleAsync(t => t.Id == tenants["tenant-a"].Id)).IsActive = false);

        // 3. The anonymous request passes the (stale) middleware — 200 from the controller, not
        //    403 TENANT_INACTIVE — but AuthService re-reads the Master DB and issues nothing.
        var again = await ForgotAsync("tenant-a");

        Assert.Equal(200, again.Status);
        Assert.Null(TokenOf(again));
        Assert.Equal(Generic, again.Json.GetProperty("message").GetString());
        var hashAfter = await ReadDbAsync(db => db.Set<AppUser>().AsNoTracking().Where(u => u.Id == userId)
            .Select(u => u.PasswordResetTokenHash).SingleAsync());
        Assert.Equal(hashBefore, hashAfter);
    }

    [Fact]
    public async Task Http_StaleMiddlewareCache_TenantDeactivatedAfterIssuance_RedemptionRejected()
    {
        var (_, tenants) = await SeedAsync(["tenant-a"]);
        var token = TokenOf(await ForgotAsync("tenant-a")); // also caches tenant-a as active
        await WithDbAsync(async db =>
            (await db.Set<TenantEntity>().SingleAsync(t => t.Id == tenants["tenant-a"].Id)).IsActive = false);

        var reset = await ResetAsync("tenant-a", token!);

        Assert.Equal(400, reset.Status); // reached the controller (stale cache), refused by AuthService
        Assert.Equal("Invalid or expired password reset token.", reset.Json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Http_MissingTenantHeader_RejectedByMiddleware_ContractUnchanged()
    {
        await SeedAsync(["tenant-a"]);

        var forgot = await ForgotAsync(tenant: null);
        var reset = await ResetAsync(tenant: null, token: "x");

        Assert.Equal(400, forgot.Status);
        Assert.Equal("TENANT_HEADER_MISSING", forgot.Json.GetProperty("code").GetString());
        Assert.Equal(400, reset.Status);
        Assert.Equal("TENANT_HEADER_MISSING", reset.Json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Http_UnknownAccount_And_NonMember_ReturnByteIdenticalBodies()
    {
        await SeedAsync(["tenant-a"], alsoExisting: ["tenant-z"]);

        var unknownAccount = await ForgotAsync("tenant-a", email: "nobody@example.com");
        var nonMember = await ForgotAsync("tenant-z");

        Assert.Equal(200, unknownAccount.Status);
        Assert.Equal(200, nonMember.Status);
        Assert.Equal(unknownAccount.Body, nonMember.Body);
    }

    [Fact]
    public async Task Http_RedemptionRefusals_ReturnByteIdenticalBodies()
    {
        await SeedAsync(["tenant-a"], alsoExisting: ["tenant-z"]);
        var token = TokenOf(await ForgotAsync("tenant-a"));

        var unknownAccount = await ResetAsync("tenant-a", token!, email: "nobody@example.com");
        var wrongTenant = await ResetAsync("tenant-z", token!);
        var wrongToken = await ResetAsync("tenant-a", "wrong");

        Assert.All(new[] { unknownAccount, wrongTenant, wrongToken }, r => Assert.Equal(400, r.Status));
        Assert.Equal(unknownAccount.Body, wrongTenant.Body);
        Assert.Equal(unknownAccount.Body, wrongToken.Body);
    }

    [Fact]
    public async Task Http_Production_IssuedAndRefused_ReturnByteIdenticalBodies()
    {
        using var production = new AuthController_PasswordRecoveryPipelineTests(Environments.Production);
        await production.SeedAsync(["tenant-a"], alsoExisting: ["tenant-z"]);

        var issued = await production.ForgotAsync("tenant-a");
        var nonMember = await production.ForgotAsync("tenant-z");
        var unknown = await production.ForgotAsync("tenant-a", email: "nobody@example.com");

        Assert.Null(TokenOf(issued)); // never exposed in Production
        Assert.Equal(issued.Body, nonMember.Body);
        Assert.Equal(issued.Body, unknown.Body);
    }

    [Fact]
    public async Task Http_C1_SaveFailureForValidMember_Returns200_ByteIdenticalToUnknownAccount()
    {
        // Before C1 a database failure while saving the token surfaced only for a valid active
        // member (an account-dependent error). Seeding goes through WithDbAsync (real DbContext
        // SaveChanges), so only AuthService's IMasterUnitOfWork saves fail.
        using var failing = new AuthController_PasswordRecoveryPipelineTests(Environments.Production, failSaves: true);
        var (userId, _) = await failing.SeedAsync(["tenant-a"]);

        var member = await failing.ForgotAsync("tenant-a");
        var unknown = await failing.ForgotAsync("tenant-a", email: "nobody@example.com");

        Assert.Equal(200, member.Status);
        Assert.Equal(200, unknown.Status);
        Assert.Equal(unknown.Body, member.Body);

        // The member's request really did reach the failing save (and nothing was written) —
        // so the identical bodies above compare a save failure against a refusal.
        var stored = await failing.ReadDbAsync(db => db.Set<AppUser>().AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.PasswordResetTokenHash).SingleAsync());
        Assert.Null(stored);
    }
}
