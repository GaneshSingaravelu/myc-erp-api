using System.Security.Claims;
using System.Text.Json;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Infrastructure.Tenant;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
}
