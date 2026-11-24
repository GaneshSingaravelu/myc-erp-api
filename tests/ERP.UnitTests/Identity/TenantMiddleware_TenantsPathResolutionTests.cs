using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using ERP.Application.Abstractions.Identity;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Entities;
using ERP.Infrastructure.Tenant;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TenantEntity = ERP.Identity.Entities.Tenant;

namespace ERP.UnitTests.Identity;

/// <summary>
/// S-T2A — "/api/v1/tenants" no longer skips tenant resolution.
///
/// The Tenants routes were listed in <c>ShouldSkipTenantResolution</c> since the commit that
/// introduced tenant provisioning. The consequence was not merely a missing header check:
/// the whole of TenantMiddleware was bypassed for that path, so an inactive tenant was never
/// rejected, the X-Tenant-Id / JWT cross-check never ran, and <c>Items["CurrentTenant"]</c>
/// was never populated — which is why ITenantProvider throws there and why the tenant-scoping
/// stages that follow could not be built.
///
/// These tests exercise TenantMiddleware DIRECTLY: a RequestDelegate, a stubbed ITenantStore
/// and a real MemoryCache. They deliberately prove only what that seam can prove — that
/// resolution is now ATTEMPTED for the Tenants paths and that its own refusals behave as
/// documented.
///
/// They do NOT prove the full HTTP pipeline. 401 for an unauthenticated caller and 403 for a
/// Viewer are decided by UseAuthorization, which runs BEFORE this middleware and is not
/// instantiated here. Those layers stay runtime proof for S-T5; nothing below pretends
/// otherwise.
/// </summary>
public class TenantMiddleware_TenantsPathResolutionTests
{
    private const string Header = "X-Tenant-Id";
    private const string KnownTenantCode = "dev-tenant";

    private readonly Mock<ITenantStore> _store = new(MockBehavior.Strict);
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private bool _nextCalled;

    private TenantMiddleware Sut() => new(
        _ =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        },
        NullLogger<TenantMiddleware>.Instance);

    private static TenantInfo Tenant(bool isActive = true) => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Identifier = KnownTenantCode,
        Name = "Dev Tenant",
        ConnectionString = "Server=(local);Database=ERP_Dev",
        DbProvider = "SqlServer",
        IsActive = isActive,
    };

    private static DefaultHttpContext ContextFor(string path, string? tenantHeader = null, string? tokenTenant = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (tenantHeader is not null)
        {
            context.Request.Headers[Header] = tenantHeader;
        }

        if (tokenTenant is not null)
        {
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("tenant_id", tokenTenant)], authenticationType: "Test"));
        }

        return context;
    }

    /// <summary>Reads the `code` member of the JSON body the middleware short-circuited with.</summary>
    private static string? CodeOf(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(body)) return null;
        return JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var code)
            ? code.GetString()
            : null;
    }

    /// <summary>The three Tenants routes, including the two withdrawn in S-T1.</summary>
    public static TheoryData<string> TenantsPaths() =>
    [
        "/api/v1/tenants",
        "/api/v1/tenants/11111111-1111-1111-1111-111111111111",
        "/api/v1/tenants/11111111-1111-1111-1111-111111111111/migrate",
    ];

    // ── The Tenants paths are no longer skipped ──────────────────────────────

    [Theory]
    [MemberData(nameof(TenantsPaths))]
    public async Task TenantsPath_WithoutHeader_IsNoLongerSkipped(string path)
    {
        var context = ContextFor(path);

        await Sut().InvokeAsync(context, _store.Object, _cache);

        // Skipping would have called next with no header check at all.
        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("TENANT_HEADER_MISSING", CodeOf(context));
    }

    [Fact]
    public async Task TenantsPath_IsMatchedCaseInsensitively()
    {
        // The middleware lowercases Request.Path before testing, so the route's actual
        // casing ("/api/v1/Tenants" from [Route("api/v1/[controller]")]) is covered too.
        var context = ContextFor("/api/v1/Tenants");

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task TenantsPath_WithUnknownTenant_Returns404()
    {
        _store.Setup(s => s.GetByIdentifierAsync("no-such-tenant")).ReturnsAsync((TenantInfo?)null);
        var context = ContextFor("/api/v1/tenants", tenantHeader: "no-such-tenant");

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("TENANT_NOT_FOUND", CodeOf(context));
    }

    [Fact]
    public async Task TenantsPath_WithInactiveTenant_Returns403()
    {
        // The loophole this closes: while the path was skipped, an inactive tenant's Admin
        // could still reach every Tenants endpoint.
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant(isActive: false));
        var context = ContextFor("/api/v1/tenants", tenantHeader: KnownTenantCode);

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_INACTIVE", CodeOf(context));
    }

    [Fact]
    public async Task TenantsPath_WithHeaderTokenMismatch_Returns403()
    {
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant());
        var context = ContextFor("/api/v1/tenants", tenantHeader: KnownTenantCode, tokenTenant: "pg-tenant");

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(context));
    }

    [Theory]
    [MemberData(nameof(TenantsPaths))]
    public async Task TenantsPath_WithValidMatchingTenant_PopulatesCurrentTenantAndContinues(string path)
    {
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant());
        var context = ContextFor(path, tenantHeader: KnownTenantCode, tokenTenant: KnownTenantCode);

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.True(_nextCalled);
        var current = Assert.IsType<TenantInfo>(context.Items["CurrentTenant"]);
        Assert.Equal(KnownTenantCode, current.Identifier);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task TenantsPath_ResolvesForAnUnauthenticatedCaller_TooAuthorizationDecidesThatEarlier()
    {
        // No principal: the JWT cross-check is skipped by design, and resolution still
        // succeeds. In the real pipeline UseAuthorization would already have returned 401
        // before this middleware ran — that layer is NOT exercised here.
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant());
        var context = ContextFor("/api/v1/tenants", tenantHeader: KnownTenantCode);

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.True(_nextCalled);
        Assert.NotNull(context.Items["CurrentTenant"]);
    }

    // ── The legitimate exclusions still skip ─────────────────────────────────

    public static TheoryData<string> StillSkippedPaths() =>
    [
        "/health",
        "/health/ready",
        "/swagger",
        "/swagger/index.html",
        "/api/v1/auth/login",
        "/hubs/notifications",
    ];

    [Theory]
    [MemberData(nameof(StillSkippedPaths))]
    public async Task LegitimateExclusions_StillSkipResolution(string path)
    {
        // Strict store mock: any lookup at all would fail the test, which is the point —
        // these paths must not require or resolve a tenant.
        var context = ContextFor(path);

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.True(_nextCalled);
        Assert.Null(context.Items["CurrentTenant"]);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OtherApiRoutes_AreUnaffectedAndStillResolve()
    {
        // Users/Roles already went through the middleware; removing the Tenants entry must
        // not have changed them. Guards against an over-broad edit to the predicate.
        foreach (var path in new[] { "/api/v1/users", "/api/v1/roles", "/api/v1/menus/my-menus" })
        {
            _nextCalled = false;
            var context = ContextFor(path);

            await Sut().InvokeAsync(context, _store.Object, _cache);

            Assert.False(_nextCalled);
            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        }
    }

    // ══ R2-DEV-02A — Fresh tenant status for authenticated requests ═════════════
    //
    // UNIT level: TenantMiddleware invoked directly, Strict ITenantStore mock, real
    // MemoryCache. "Cached" below means the cache entry was populated by an earlier request
    // through the middleware itself, not planted by the test, so the tests do not depend on
    // the cache-key format. Pipeline-level tests follow further down.

    private const string WorkOrdersPath = "/api/v1/work-orders";
    private const string OtherTenantCode = "pg-tenant";

    private static TenantInfo TenantFor(string code, bool isActive = true, string? id = null) => new()
    {
        Id = Guid.Parse(id ?? "22222222-2222-2222-2222-222222222222"),
        Identifier = code,
        Name = code,
        ConnectionString = "Server=(local);Database=ERP_" + code,
        DbProvider = "SqlServer",
        IsActive = isActive,
    };

    private async Task<HttpContext> SendAsync(string path, string? header, string? tokenTenant)
    {
        _nextCalled = false;
        var context = ContextFor(path, header, tokenTenant);
        await Sut().InvokeAsync(context, _store.Object, _cache);
        return context;
    }

    [Fact]
    public async Task AC01_ActiveTenant_AuthenticatedRequest_Succeeds_AndReadsStatusFresh()
    {
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant());

        var first = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.True(_nextCalled);
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(KnownTenantCode, Assert.IsType<TenantInfo>(first.Items["CurrentTenant"]).Identifier);

        var second = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.True(_nextCalled);
        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);

        // A warm cache did not short-circuit the second authenticated request.
        _store.Verify(s => s.GetByIdentifierAsync(KnownTenantCode), Times.Exactly(2));
    }

    [Fact]
    public async Task AC02_CachedActiveTenant_DeactivatedInDb_NextAuthenticatedRequestIsDenied()
    {
        _store.SetupSequence(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ReturnsAsync(Tenant(isActive: true))
            .ReturnsAsync(Tenant(isActive: false));

        await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode); // populates the cache as active
        Assert.True(_nextCalled);

        var denied = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response.StatusCode);
        Assert.Equal("TENANT_INACTIVE", CodeOf(denied));
        Assert.Null(denied.Items["CurrentTenant"]);
    }

    [Fact]
    public async Task AC02_DeactivationIsAlsoWrittenToTheCache_SoAnonymousRequestsSeeItToo()
    {
        _store.SetupSequence(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ReturnsAsync(Tenant(isActive: true))
            .ReturnsAsync(Tenant(isActive: false));

        await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode); // cached active
        await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode); // fresh read: inactive

        // Anonymous request: served from the cache (no third store call — Strict sequence is
        // exhausted), and the cache now holds the fresh inactive status.
        var anonymous = await SendAsync("/api/v1/auth/refresh", KnownTenantCode, tokenTenant: null);

        Assert.False(_nextCalled);
        Assert.Equal("TENANT_INACTIVE", CodeOf(anonymous));
        _store.Verify(s => s.GetByIdentifierAsync(KnownTenantCode), Times.Exactly(2));
    }

    [Fact]
    public async Task AC03_CachedTenant_MissingFromDb_NextAuthenticatedRequestIsDenied_AndCacheEntryDropped()
    {
        _store.SetupSequence(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ReturnsAsync(Tenant())
            .ReturnsAsync((TenantInfo?)null)
            .ReturnsAsync((TenantInfo?)null);

        await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode); // cached active
        Assert.True(_nextCalled);

        var denied = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, denied.Response.StatusCode);
        Assert.Equal("TENANT_NOT_FOUND", CodeOf(denied));

        // The stale "active" entry was removed: an anonymous request now misses the cache
        // and goes to the store (third call) instead of being served the old entry.
        var anonymous = await SendAsync("/api/v1/auth/refresh", KnownTenantCode, tokenTenant: null);
        Assert.False(_nextCalled);
        Assert.Equal("TENANT_NOT_FOUND", CodeOf(anonymous));
        _store.Verify(s => s.GetByIdentifierAsync(KnownTenantCode), Times.Exactly(3));
    }

    [Fact]
    public async Task AC04_ReactivatedTenant_FollowsFreshStatus()
    {
        // The middleware only decides on the tenant's status; it does not revoke sessions or
        // tokens. An access token that is still valid works again once the tenant is active
        // again. Session revocation on the refresh path (PR #33) is AuthService's concern and
        // is covered by AuthService_TenantStatusTests.
        _store.SetupSequence(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ReturnsAsync(Tenant(isActive: true))
            .ReturnsAsync(Tenant(isActive: false))
            .ReturnsAsync(Tenant(isActive: true));

        await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.True(_nextCalled);

        var denied = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.False(_nextCalled);
        Assert.Equal("TENANT_INACTIVE", CodeOf(denied));

        var allowed = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.True(_nextCalled);
        Assert.Equal(StatusCodes.Status200OK, allowed.Response.StatusCode);
    }

    [Fact]
    public async Task AC05_HeaderTokenMismatch_IsRejectedBeforeAnyLookup_NoStatusDisclosure()
    {
        // Strict mock with NO setup for the other tenant: the mismatch is decided without
        // reading the other tenant at all, so whether it is active (or exists) cannot leak.
        var context = await SendAsync(WorkOrdersPath, OtherTenantCode, KnownTenantCode);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(context));
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AC05_HeaderTokenMismatch_ForNonexistentTenant_Returns403Mismatch_Not404()
    {
        var context = await SendAsync(WorkOrdersPath, "no-such-tenant", KnownTenantCode);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(context));
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AC05_HeaderMatchesTokenCaseInsensitively_StillAllowed()
    {
        _store.Setup(s => s.GetByIdentifierAsync("DEV-TENANT")).ReturnsAsync(Tenant());

        var context = await SendAsync(WorkOrdersPath, "DEV-TENANT", KnownTenantCode);

        Assert.True(_nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AC06_MissingHeader_AuthenticatedRequest_IsRejected(string? header)
    {
        var context = await SendAsync(WorkOrdersPath, header, KnownTenantCode);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("TENANT_HEADER_MISSING", CodeOf(context));
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AC07_TenantADeactivated_DoesNotAffectTenantB()
    {
        _store.SetupSequence(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ReturnsAsync(Tenant(isActive: true))
            .ReturnsAsync(Tenant(isActive: false));
        _store.Setup(s => s.GetByIdentifierAsync(OtherTenantCode)).ReturnsAsync(TenantFor(OtherTenantCode));

        await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        await SendAsync(WorkOrdersPath, OtherTenantCode, OtherTenantCode);

        var a = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.False(_nextCalled);
        Assert.Equal("TENANT_INACTIVE", CodeOf(a));

        var b = await SendAsync(WorkOrdersPath, OtherTenantCode, OtherTenantCode);
        Assert.True(_nextCalled);
        Assert.Equal(OtherTenantCode, Assert.IsType<TenantInfo>(b.Items["CurrentTenant"]).Identifier);
    }

    [Fact]
    public async Task AC08_AuthoritativeLookupFails_RequestDenied_NoStaleCacheFallback()
    {
        _store.SetupSequence(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ReturnsAsync(Tenant(isActive: true))
            .ThrowsAsync(new InvalidOperationException("master database unavailable"));

        await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode); // cache holds "active"
        Assert.True(_nextCalled);

        var context = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Null(context.Items["CurrentTenant"]);

        // Read the body once (CodeOf closes the stream): correct code, no exception details.
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal("TENANT_STATUS_UNAVAILABLE", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("master database unavailable", body);
    }

    [Fact]
    public async Task AC08_LookupFailsOnColdCache_RequestDenied()
    {
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ThrowsAsync(new TimeoutException("timeout"));

        var context = await SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("TENANT_STATUS_UNAVAILABLE", CodeOf(context));
    }

    [Fact]
    public async Task AC09_LoginStillSkipsTheMiddleware_EvenWhenAuthenticated()
    {
        // PR #33: login enforces tenant status itself (AuthService). The middleware must
        // keep skipping /auth/login so it neither adds a lookup nor changes login responses.
        var context = await SendAsync("/api/v1/auth/login", KnownTenantCode, KnownTenantCode);

        Assert.True(_nextCalled);
        Assert.Null(context.Items["CurrentTenant"]);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AC09_AnonymousRefresh_KeepsExistingCachedBehaviour()
    {
        // Pins the UNCHANGED behaviour for unauthenticated requests: /auth/refresh is called
        // with an expired access token, so the caller is not authenticated here and the cache
        // is used. The authoritative tenant check for refresh is AuthService's own fresh
        // database read added in PR #33 (AuthService_TenantStatusTests), not this middleware.
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant());

        await SendAsync("/api/v1/auth/refresh", KnownTenantCode, tokenTenant: null);
        Assert.True(_nextCalled);
        await SendAsync("/api/v1/auth/refresh", KnownTenantCode, tokenTenant: null);
        Assert.True(_nextCalled);

        _store.Verify(s => s.GetByIdentifierAsync(KnownTenantCode), Times.Once);
    }

    [Theory]
    [MemberData(nameof(TenantsPaths))]
    public async Task AC10_TenantsPaths_AlsoGetFreshStatus(string path)
    {
        // PR #14 put the Tenants routes through this middleware; they get the same fresh
        // check. Endpoint-level restrictions (withdrawn operations, [Authorize]) live in the
        // controller and authorization layers, which this change does not touch.
        _store.SetupSequence(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ReturnsAsync(Tenant(isActive: true))
            .ReturnsAsync(Tenant(isActive: false));

        await SendAsync(path, KnownTenantCode, KnownTenantCode);
        Assert.True(_nextCalled);

        var denied = await SendAsync(path, KnownTenantCode, KnownTenantCode);
        Assert.False(_nextCalled);
        Assert.Equal("TENANT_INACTIVE", CodeOf(denied));
    }

    [Theory]
    [MemberData(nameof(StillSkippedPaths))]
    public async Task AC10_Exclusions_StillSkip_ForAuthenticatedCallersToo(string path)
    {
        var context = await SendAsync(path, KnownTenantCode, KnownTenantCode);

        Assert.True(_nextCalled);
        Assert.Null(context.Items["CurrentTenant"]);
        _store.VerifyNoOtherCalls();
    }

    // ══ R2-DEV-02A — PIPELINE level ═══════════════════════════════════════════════
    //
    // A real ApplicationBuilder running UseMiddleware<TenantMiddleware>() (so ITenantStore and
    // IMemoryCache are method-injected from a real per-request DI scope, as in Program.cs),
    // the real MasterDbTenantStore, a real singleton MemoryCache, and a MasterDbContext on
    // EF Core InMemory. Each request gets its own scope and DbContext.
    //
    // NOT covered here: Kestrel/TestServer, UseAuthentication/UseAuthorization (the principal
    // is set directly), GlobalExceptionMiddleware, and real SQL Server/PostgreSQL. The test
    // project has no Microsoft.AspNetCore.TestHost reference and the approved scope adds no
    // packages, so a full HTTP-host test is not included.

    private sealed class Pipeline : IDisposable
    {
        private readonly ServiceProvider _root;
        private readonly RequestDelegate _app;
        public bool Reached { get; private set; }

        public Pipeline()
        {
            // Registers the Identity entity configurations once (thread-safe static ctor),
            // exactly as AuthServiceTestBase does for its own InMemory contexts.
            RuntimeHelpers.RunClassConstructor(typeof(AuthServiceTestBase).TypeHandle);

            var dbName = Guid.NewGuid().ToString();
            var currentUser = new Mock<ICurrentUserService>();
            currentUser.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddDbContext<MasterDbContext>(o => o
                .UseInMemoryDatabase(dbName)
                .AddInterceptors(new AuditableEntityInterceptor(currentUser.Object)));
            services.AddScoped<ITenantStore, MasterDbTenantStore>();
            _root = services.BuildServiceProvider();

            var builder = new ApplicationBuilder(_root);
            builder.UseMiddleware<TenantMiddleware>();
            builder.Run(ctx =>
            {
                Reached = true;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });
            _app = builder.Build();
        }

        public async Task WithDbAsync(Func<MasterDbContext, Task> action)
        {
            using var scope = _root.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
            await action(db);
            await db.SaveChangesAsync();
        }

        public async Task<HttpContext> SendAsync(string path, string tenantHeader, string? tokenTenant)
        {
            Reached = false;
            using var scope = _root.CreateScope();
            var context = ContextFor(path, tenantHeader, tokenTenant);
            context.RequestServices = scope.ServiceProvider;
            await _app(context);
            return context;
        }

        public async Task<HttpContext> SendAuthenticatedAsync(string path, string tenantHeader, params Claim[] claims)
        {
            Reached = false;
            using var scope = _root.CreateScope();
            var context = AuthenticatedContextFor(path, tenantHeader, claims);
            context.RequestServices = scope.ServiceProvider;
            await _app(context);
            return context;
        }

        public void Dispose() => _root.Dispose();
    }

    private static TenantEntity NewTenantEntity(string code) => new()
    {
        Code = TenantCode.Normalize(code),
        Name = code,
        ConnectionString = "Server=(local);Database=ERP_" + code,
        DbProvider = "SqlServer",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Pipeline_AC01_AC02_AC04_DeactivateThenReactivateInMasterDb_FollowsFreshStatus()
    {
        using var pipeline = new Pipeline();
        await pipeline.WithDbAsync(db => db.Set<TenantEntity>().AddAsync(NewTenantEntity(KnownTenantCode)).AsTask());

        var ok = await pipeline.SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.True(pipeline.Reached);
        Assert.Equal(StatusCodes.Status200OK, ok.Response.StatusCode);

        await pipeline.WithDbAsync(async db =>
            (await db.Set<TenantEntity>().SingleAsync(t => t.Code == TenantCode.Normalize(KnownTenantCode))).IsActive = false);

        var denied = await pipeline.SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.False(pipeline.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response.StatusCode);
        Assert.Equal("TENANT_INACTIVE", CodeOf(denied));

        await pipeline.WithDbAsync(async db =>
            (await db.Set<TenantEntity>().SingleAsync(t => t.Code == TenantCode.Normalize(KnownTenantCode))).IsActive = true);

        var again = await pipeline.SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.True(pipeline.Reached);
        Assert.Equal(StatusCodes.Status200OK, again.Response.StatusCode);
    }

    [Fact]
    public async Task Pipeline_AC03_TenantRemovedFromMasterDb_NextAuthenticatedRequestDenied()
    {
        using var pipeline = new Pipeline();
        await pipeline.WithDbAsync(db => db.Set<TenantEntity>().AddAsync(NewTenantEntity(KnownTenantCode)).AsTask());

        await pipeline.SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.True(pipeline.Reached);

        await pipeline.WithDbAsync(async db =>
            db.Set<TenantEntity>().Remove(await db.Set<TenantEntity>().SingleAsync(t => t.Code == TenantCode.Normalize(KnownTenantCode))));

        var denied = await pipeline.SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.False(pipeline.Reached);
        Assert.Equal(StatusCodes.Status404NotFound, denied.Response.StatusCode);
        Assert.Equal("TENANT_NOT_FOUND", CodeOf(denied));
    }

    [Fact]
    public async Task Pipeline_AC07_DeactivatingTenantA_LeavesTenantBWorking()
    {
        using var pipeline = new Pipeline();
        await pipeline.WithDbAsync(async db =>
        {
            await db.Set<TenantEntity>().AddAsync(NewTenantEntity(KnownTenantCode));
            await db.Set<TenantEntity>().AddAsync(NewTenantEntity(OtherTenantCode));
        });

        await pipeline.SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        await pipeline.SendAsync(WorkOrdersPath, OtherTenantCode, OtherTenantCode);

        await pipeline.WithDbAsync(async db =>
            (await db.Set<TenantEntity>().SingleAsync(t => t.Code == TenantCode.Normalize(KnownTenantCode))).IsActive = false);

        var a = await pipeline.SendAsync(WorkOrdersPath, KnownTenantCode, KnownTenantCode);
        Assert.False(pipeline.Reached);
        Assert.Equal("TENANT_INACTIVE", CodeOf(a));

        var b = await pipeline.SendAsync(WorkOrdersPath, OtherTenantCode, OtherTenantCode);
        Assert.True(pipeline.Reached);
        Assert.Equal(StatusCodes.Status200OK, b.Response.StatusCode);
    }

    [Fact]
    public async Task Pipeline_AC05_MismatchRejected_ThroughRealPipeline()
    {
        using var pipeline = new Pipeline();
        await pipeline.WithDbAsync(db => db.Set<TenantEntity>().AddAsync(NewTenantEntity(KnownTenantCode)).AsTask());

        var context = await pipeline.SendAsync(WorkOrdersPath, KnownTenantCode, OtherTenantCode);

        Assert.False(pipeline.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(context));
    }

    // ══ R2-DEV-02B-1 — Mandatory tenant_id claim ═════════════════════════════════
    //
    // UNIT level, same seam as above. An authenticated request must carry a non-blank
    // tenant_id claim that matches X-Tenant-Id; otherwise it gets the existing
    // 403 TENANT_MISMATCH, decided before the cache or the store is touched.

    /// <summary>
    /// An AUTHENTICATED context with exactly the claims given — unlike <see cref="ContextFor"/>,
    /// which only builds a principal when it has a tenant claim to add.
    /// </summary>
    private static DefaultHttpContext AuthenticatedContextFor(string path, string? tenantHeader, params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (tenantHeader is not null)
            context.Request.Headers[Header] = tenantHeader;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            claims.Length == 0 ? [new Claim("sub", "user-1")] : claims,
            authenticationType: "Test"));
        return context;
    }

    private async Task<HttpContext> SendAuthenticatedAsync(string path, string? header, params Claim[] claims)
    {
        _nextCalled = false;
        var context = AuthenticatedContextFor(path, header, claims);
        await Sut().InvokeAsync(context, _store.Object, _cache);
        return context;
    }

    private void AssertRejectedAsMismatch(HttpContext context)
    {
        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(context));
        Assert.Null(context.Items["CurrentTenant"]);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task B01_MissingTenantClaim_AuthenticatedRequest_Returns403Mismatch()
    {
        var context = await SendAuthenticatedAsync(WorkOrdersPath, KnownTenantCode, new Claim("sub", "user-1"));

        Assert.True(context.User.Identity!.IsAuthenticated);
        AssertRejectedAsMismatch(context);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task B02_EmptyOrWhitespaceTenantClaim_Returns403Mismatch(string claimValue)
    {
        var context = await SendAuthenticatedAsync(WorkOrdersPath, KnownTenantCode,
            new Claim("sub", "user-1"), new Claim("tenant_id", claimValue));

        AssertRejectedAsMismatch(context);
    }

    [Fact]
    public async Task B03_MismatchedTenantClaim_Returns403Mismatch()
    {
        var context = await SendAuthenticatedAsync(WorkOrdersPath, KnownTenantCode,
            new Claim("sub", "user-1"), new Claim("tenant_id", OtherTenantCode));

        AssertRejectedAsMismatch(context);
    }

    [Fact]
    public async Task B04_MatchingClaim_ActiveTenant_IsAllowed()
    {
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant());

        var context = await SendAuthenticatedAsync(WorkOrdersPath, KnownTenantCode,
            new Claim("sub", "user-1"), new Claim("tenant_id", KnownTenantCode));

        Assert.True(_nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(KnownTenantCode, Assert.IsType<TenantInfo>(context.Items["CurrentTenant"]).Identifier);
        _store.Verify(s => s.GetByIdentifierAsync(KnownTenantCode), Times.Once);
    }

    public static TheoryData<string?> InvalidClaimValues() => [null, "", "   ", OtherTenantCode];

    [Theory]
    [MemberData(nameof(InvalidClaimValues))]
    public async Task B05_InvalidClaim_NoMasterDbLookup_AndNoTenantCacheAccess(string? claimValue)
    {
        // Strict mocks for BOTH the store and the cache: any lookup, read or write fails the test.
        var strictCache = new Mock<IMemoryCache>(MockBehavior.Strict);
        var claims = claimValue is null
            ? new[] { new Claim("sub", "user-1") }
            : new[] { new Claim("sub", "user-1"), new Claim("tenant_id", claimValue) };
        var context = AuthenticatedContextFor(WorkOrdersPath, KnownTenantCode, claims);

        await Sut().InvokeAsync(context, _store.Object, strictCache.Object);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(context));
        _store.VerifyNoOtherCalls();
        strictCache.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("DEV-TENANT", "dev-tenant")]
    [InlineData("dev-tenant", "DEV-TENANT")]
    [InlineData("Dev-Tenant", "dEV-tENANT")]
    public async Task B06_CaseInsensitiveMatch_StillAllowed(string header, string claim)
    {
        _store.Setup(s => s.GetByIdentifierAsync(header)).ReturnsAsync(Tenant());

        var context = await SendAuthenticatedAsync(WorkOrdersPath, header,
            new Claim("sub", "user-1"), new Claim("tenant_id", claim));

        Assert.True(_nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task B07_AnonymousRequest_NoPrincipal_KeepsCachedBehaviour()
    {
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant());

        var first = await SendAsync("/api/v1/auth/refresh", KnownTenantCode, tokenTenant: null);
        Assert.True(_nextCalled);
        var second = await SendAsync("/api/v1/auth/refresh", KnownTenantCode, tokenTenant: null);
        Assert.True(_nextCalled);

        Assert.NotNull(first.Items["CurrentTenant"]);
        Assert.NotNull(second.Items["CurrentTenant"]);
        _store.Verify(s => s.GetByIdentifierAsync(KnownTenantCode), Times.Once); // second served from cache
    }

    [Fact]
    public async Task B07_UnauthenticatedIdentityWithoutClaim_IsTreatedAsAnonymous_NotRejected()
    {
        // A principal whose identity is NOT authenticated (no authentication type) is an
        // anonymous caller: the claim requirement applies only to authenticated requests.
        _store.Setup(s => s.GetByIdentifierAsync(KnownTenantCode)).ReturnsAsync(Tenant());
        _nextCalled = false;
        var context = ContextFor("/api/v1/auth/refresh", KnownTenantCode);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")]));

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.False(context.User.Identity!.IsAuthenticated);
        Assert.True(_nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task B07_AnonymousRequest_MissingHeader_StillReturns400()
    {
        var context = await SendAsync(WorkOrdersPath, header: null, tokenTenant: null);

        Assert.False(_nextCalled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("TENANT_HEADER_MISSING", CodeOf(context));
    }

    [Theory]
    [MemberData(nameof(StillSkippedPaths))]
    public async Task B08_SkippedPaths_IncludingLoginAndSignalR_StillSkip_EvenWithoutClaim(string path)
    {
        // /api/v1/auth/login (PR #33) and /hubs/ (SignalR) keep skipping the middleware, so the
        // new claim requirement does not reach them.
        var context = await SendAuthenticatedAsync(path, KnownTenantCode, new Claim("sub", "user-1"));

        Assert.True(_nextCalled);
        Assert.Null(context.Items["CurrentTenant"]);
        _store.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(TenantsPaths))]
    public async Task B08_TenantsPaths_MissingClaim_Rejected(string path)
    {
        // PR #14 routes the Tenants paths through the middleware; they get the same rule.
        var context = await SendAuthenticatedAsync(path, KnownTenantCode, new Claim("sub", "user-1"));

        AssertRejectedAsMismatch(context);
    }

    [Fact]
    public async Task B08_FreshStatusEnforcement_StillAppliesWithValidClaim()
    {
        // PR #34: a valid, matching claim still gets the fresh status read.
        _store.SetupSequence(s => s.GetByIdentifierAsync(KnownTenantCode))
            .ReturnsAsync(Tenant(isActive: true))
            .ReturnsAsync(Tenant(isActive: false));
        var claims = new[] { new Claim("sub", "user-1"), new Claim("tenant_id", KnownTenantCode) };

        await SendAuthenticatedAsync(WorkOrdersPath, KnownTenantCode, claims);
        Assert.True(_nextCalled);

        var denied = await SendAuthenticatedAsync(WorkOrdersPath, KnownTenantCode, claims);
        Assert.False(_nextCalled);
        Assert.Equal("TENANT_INACTIVE", CodeOf(denied));
    }

    [Fact]
    public async Task Pipeline_B01_MissingClaim_RejectedThroughRealPipeline()
    {
        using var pipeline = new Pipeline();
        await pipeline.WithDbAsync(db => db.Set<TenantEntity>().AddAsync(NewTenantEntity(KnownTenantCode)).AsTask());

        var context = await pipeline.SendAuthenticatedAsync(WorkOrdersPath, KnownTenantCode, new Claim("sub", "user-1"));

        Assert.False(pipeline.Reached);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(context));
    }
}
