using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using ERP.Application.Abstractions.Identity.Contracts;
using ERP.Application.Abstractions.MultiTenancy;
using ERP.Identity.Entities;
using ERP.Identity.Seed;
using ERP.Identity.Services;
using ERP.Infrastructure.Tenant;
using ERP.Persistence.Context;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ERP.UnitTests.Identity;

// ════════════════════════════════════════════════════════════════════════════════
// P1-f — Tenant.Code canonical form.
//
// THE DEFECT THESE TESTS PIN DOWN
// ===============================
// Tenant.Code was compared with whatever casing the caller supplied. On SQL Server that
// worked by accident of collation: the default is case-insensitive, so "DEV-TENANT" and
// "dev-tenant" resolved to the same tenant. PostgreSQL compares case-sensitively, so the
// identical request would have failed to resolve at all — the database provider would have
// decided who a tenant is.
//
// The fix canonicalizes the code in the application, on every write and every lookup, so
// both engines agree. The tests below are therefore deliberately split:
//
//   * pure semantics of the normalizer                     — TenantCodeNormalizeTests
//   * lookup + write behaviour on a CASE-SENSITIVE store   — TenantCodeRelationalTests
//   * the create path                                      — TenantCodeCreatePathTests
//   * the configuration seed path                          — TenantCodeSeedPathTests
//   * the middleware cache key                             — TenantCodeCacheKeyTests
//
// The relational class runs on SQLite, whose TEXT comparison is BINARY — case-SENSITIVE,
// like PostgreSQL. That is the point: on the EF InMemory provider or SQL Server's default
// collation, several of these tests could pass without the fix. Each class that depends on
// case-sensitivity asserts it first, so none of them can pass vacuously.
// ════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Semantics of <see cref="TenantCode.Normalize"/> in isolation: trim, invariant-lowercase,
/// and nothing else.
/// </summary>
public class TenantCodeNormalizeTests
{
    [Theory]
    // already canonical — unchanged
    [InlineData("acme", "acme")]
    [InlineData("dev-tenant", "dev-tenant")]
    [InlineData("t2", "t2")]
    // mixed and upper case — lowercased
    [InlineData("ACME", "acme")]
    [InlineData("AcMe", "acme")]
    [InlineData("DEV-TENANT", "dev-tenant")]
    [InlineData("Tenant_A", "tenant_a")]
    // surrounding whitespace — trimmed
    [InlineData("  acme", "acme")]
    [InlineData("acme  ", "acme")]
    [InlineData("  ACME  ", "acme")]
    [InlineData("\tDev-Tenant\r\n", "dev-tenant")]
    // unusable input — empty, never an identifier
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData(" ", "")]
    [InlineData("      ", "")]
    [InlineData("\t", "")]
    [InlineData("\r\n", "")]
    [InlineData(" \t \r\n ", "")]
    public void Normalize_ProducesTheCanonicalForm(string? input, string expected)
        => Assert.Equal(expected, TenantCode.Normalize(input));

    [Fact]
    public void Normalize_WithNull_DoesNotThrow()
    {
        // The callers' contract depends on this. TenantRepository.GetByCodeAsync answers
        // "not found" for an unusable code and ExistsByCodeAsync answers false; if the
        // normalizer threw, a lookup miss would become an exception instead.
        var result = TenantCode.Normalize(null);

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Normalize_NeverInventsAnIdentifierFromUnusableInput(string? input)
    {
        // The security property: no amount of whitespace becomes a meaningful tenant code.
        var result = TenantCode.Normalize(input);

        Assert.Equal(string.Empty, result);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("ACME")]
    [InlineData("  Dev-Tenant  ")]
    [InlineData("already-canonical")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Normalize_IsIdempotent(string? input)
    {
        // Matters because the value is normalized at more than one layer — a service computes
        // it and the repository normalizes again. Re-applying it must be a no-op, otherwise
        // the layer count would change the answer.
        var once = TenantCode.Normalize(input);

        Assert.Equal(once, TenantCode.Normalize(once));
    }

    [Theory]
    [InlineData("de v", "de v")]
    [InlineData("A B", "a b")]
    public void Normalize_PreservesInteriorWhitespace(string input, string expected)
    {
        // Trim, not collapse. Interior characters are part of the identifier; rewriting them
        // would silently merge two distinct codes into one.
        Assert.Equal(expected, TenantCode.Normalize(input));
    }

    [Fact]
    public void Normalize_LowercasesRatherThanUppercases()
    {
        // Stated as its own assertion because the codebase contains the OTHER convention:
        // RoleService.NormalizeRoleName is ToUpperInvariant(), and AppUser.NormalizedUserName
        // is upper too. Tenant.Code is deliberately the lower-case convention — the existing
        // seeded code is "dev-tenant" — so the two must not be confused.
        Assert.Equal("acme", TenantCode.Normalize("AcMe"));
        Assert.NotEqual("ACME", TenantCode.Normalize("AcMe"));
    }

    [Fact]
    public void Normalize_IsCultureInvariant()
    {
        // The Turkish dotless-i. Under tr-TR, "I".ToLower() is "ı" (U+0131), not "i".
        // A culture-sensitive ToLower would therefore make tenant resolution depend on the
        // server's locale — the same class of bug as depending on the database's collation.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal("tenant-i", TenantCode.Normalize("TENANT-I"));
            Assert.Equal("istanbul", TenantCode.Normalize("ISTANBUL"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

/// <summary>
/// Lookup and write behaviour against a RELATIONAL, CASE-SENSITIVE store (SQLite, BINARY
/// collation) — the provider that behaves like PostgreSQL rather than like SQL Server's
/// default collation.
/// </summary>
public class TenantCodeRelationalTests : RelationalIdentityTestBase
{
    private MasterDbTenantStore Store => new(Context);

    /// <summary>Writes a Tenant row with the code EXACTLY as given, bypassing every
    /// application write path — the direct-SQL / data-import case.</summary>
    private Task<Tenant> PersistRawAsync(string rawCode) => PersistAsync(new Tenant
    {
        Code = rawCode,
        Name = "Raw Row",
        ConnectionString = "DataSource=:memory:",
        DbProvider = "SqlServer",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
    });

    // ── Controls: this provider really is case-sensitive ─────────────────────

    [Fact]
    public async Task Control_TheStoreComparesCodesCaseSensitively()
    {
        // Asserted rather than assumed. If this provider were case-insensitive (as SQL
        // Server's default collation is), every test below could pass without the fix.
        await PersistRawAsync("ACME");

        Assert.True(await Context.Set<Tenant>().AnyAsync(t => t.Code == "ACME"));
        Assert.False(await Context.Set<Tenant>().AnyAsync(t => t.Code == "acme"));
    }

    [Fact]
    public async Task Control_TheUniqueIndexAloneDoesNotEnforceTheInvariant()
    {
        // Tenant.Code carries a unique index (IdentityEntityConfigurations: HasIndex(t =>
        // t.Code).IsUnique()). On a case-sensitive engine that index happily accepts "acme"
        // AND "ACME" as two different tenants. THIS is why normalizing on write is required
        // and not merely tidy: the index cannot express the invariant by itself.
        await PersistRawAsync("acme");
        await PersistRawAsync("ACME");

        Assert.Equal(2, await Context.Set<Tenant>().CountAsync());
    }

    // ── TenantRepository.GetByCodeAsync ──────────────────────────────────────

    [Theory]
    [InlineData("acme")]
    [InlineData("ACME")]
    [InlineData("AcMe")]
    [InlineData("  ACME  ")]
    [InlineData("\tacme\r\n")]
    public async Task GetByCodeAsync_ResolvesAnyCasingOrPadding(string supplied)
    {
        var seeded = await PersistAsync(CreateTestTenant("ACME")); // stored canonical: "acme"
        Assert.Equal("acme", seeded.Code);

        var found = await Tenants.GetByCodeAsync(supplied);

        Assert.NotNull(found);
        Assert.Equal(seeded.Id, found.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetByCodeAsync_WithUnusableCode_ReturnsNotFoundRatherThanThrowing(string? supplied)
    {
        await PersistAsync(CreateTestTenant("ACME"));

        var found = await Tenants.GetByCodeAsync(supplied!);

        Assert.Null(found);
    }

    [Fact]
    public async Task GetByCodeAsync_StillDistinguishesDifferentTenants()
    {
        // Normalization must not collapse genuinely different codes into one.
        var acme = await PersistAsync(CreateTestTenant("ACME"));
        var other = await PersistAsync(CreateTestTenant("Globex"));

        Assert.Equal(acme.Id, (await Tenants.GetByCodeAsync("acme"))!.Id);
        Assert.Equal(other.Id, (await Tenants.GetByCodeAsync("GLOBEX"))!.Id);
        Assert.Null(await Tenants.GetByCodeAsync("initech"));
    }

    [Fact]
    public async Task GetByCodeAsync_CannotReachANonCanonicalRow()
    {
        // The stated residual risk, asserted instead of hidden. A row written outside the
        // application — direct SQL, an import, a manual fix — can still hold a non-canonical
        // code, and because lookups canonicalize, that row is unreachable. Database-level
        // enforcement (CHECK / generated column / citext / collation) is deliberately NOT
        // part of this slice, so this is the documented consequence, not a surprise.
        await PersistRawAsync("ACME");

        Assert.Null(await Tenants.GetByCodeAsync("ACME"));
        Assert.Null(await Tenants.GetByCodeAsync("acme"));
    }

    // ── TenantRepository.ExistsByCodeAsync ───────────────────────────────────

    [Theory]
    [InlineData("acme", true)]
    [InlineData("ACME", true)]
    [InlineData("  AcMe ", true)]
    [InlineData("globex", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public async Task ExistsByCodeAsync_IsCaseAndPaddingInsensitive(string supplied, bool expected)
    {
        await PersistAsync(CreateTestTenant("ACME"));

        Assert.Equal(expected, await Tenants.ExistsByCodeAsync(supplied));
    }

    [Fact]
    public async Task ExistsByCodeAsync_WithNull_ReturnsFalseRatherThanThrowing()
    {
        await PersistAsync(CreateTestTenant("ACME"));

        Assert.False(await Tenants.ExistsByCodeAsync(null!));
    }

    // ── MasterDbTenantStore.GetByIdentifierAsync (the X-Tenant-Id path) ──────

    [Theory]
    [InlineData("dev-tenant")]
    [InlineData("DEV-TENANT")]
    [InlineData("Dev-Tenant")]
    [InlineData("  dev-tenant  ")]
    public async Task GetByIdentifierAsync_ResolvesAnyCasingOrPadding(string header)
    {
        var seeded = await PersistAsync(CreateTestTenant("DEV-TENANT"));

        var info = await Store.GetByIdentifierAsync(header);

        Assert.NotNull(info);
        Assert.Equal(seeded.Id, info.Id);
        // The resolved identity carries the CANONICAL code, not the header's casing —
        // downstream code and the JWT tenant claim therefore see one stable value.
        Assert.Equal("dev-tenant", info.Identifier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetByIdentifierAsync_WithUnusableIdentifier_ReturnsNull(string? header)
    {
        await PersistAsync(CreateTestTenant("DEV-TENANT"));

        Assert.Null(await Store.GetByIdentifierAsync(header!));
    }

    [Fact]
    public async Task GetByIdentifierAsync_StillRejectsAnUnknownTenant()
    {
        await PersistAsync(CreateTestTenant("DEV-TENANT"));

        Assert.Null(await Store.GetByIdentifierAsync("NO-SUCH-TENANT"));
    }
}

/// <summary>
/// The create path. Unreachable over HTTP since phase S-T1 withdrew the route; these tests
/// exercise the surviving primitive directly, because a dead primitive must not be unsafe if
/// it is ever re-exposed.
/// </summary>
public class TenantCodeCreatePathTests : IdentityServiceTestBase
{
    private readonly TenantManagementService _sut;

    public TenantCodeCreatePathTests()
        => _sut = new TenantManagementService(
            Tenants, UnitOfWork, TenantProvider, NullLogger<TenantManagementService>.Instance);

    private static CreateTenantRequest Request(string code)
        => new(code, "Acme Industries", "Server=(local);Database=ERP_Acme", "SqlServer");

    [Theory]
    [InlineData("ACME", "acme")]
    [InlineData("AcMe", "acme")]
    [InlineData("  ACME  ", "acme")]
    [InlineData("acme", "acme")]
    public async Task CreateAsync_PersistsTheCanonicalCode(string supplied, string expected)
    {
        var result = await _sut.CreateAsync(Request(supplied));

        Assert.True(result.IsSuccess);
        var stored = await Context.Set<Tenant>().SingleAsync();
        Assert.Equal(expected, stored.Code);
    }

    [Theory]
    [InlineData("ACME")]
    [InlineData("AcMe")]
    [InlineData("  acme ")]
    public async Task CreateAsync_RejectsADuplicateThatDiffersOnlyInCaseOrPadding(string supplied)
    {
        await PersistAsync(CreateTestTenant("acme"));

        var result = await _sut.CreateAsync(Request(supplied));

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.CodeExists", result.Error.Code);
        Assert.Single(await Context.Set<Tenant>().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_ConflictMessageEchoesTheSuppliedCodeNotTheRewrittenOne()
    {
        // The caller sent "ACME"; reporting a silently rewritten "acme" back at them would
        // read like a different conflict than the one they caused.
        await PersistAsync(CreateTestTenant("acme"));

        var result = await _sut.CreateAsync(Request("ACME"));

        Assert.Contains("ACME", result.Error.Message);
    }

    [Fact]
    public async Task CreateAsync_StillAcceptsAGenuinelyDifferentCode()
    {
        await PersistAsync(CreateTestTenant("acme"));

        var result = await _sut.CreateAsync(Request("GLOBEX"));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, await Context.Set<Tenant>().CountAsync());
    }
}

/// <summary>
/// The configuration seed path — the only write path that actually runs today, and the one
/// that produced the "dev-tenant" row the application resolves against.
/// </summary>
public class TenantCodeSeedPathTests : IDisposable
{
    private readonly MasterDbContext _context;

    static TenantCodeSeedPathTests()
    {
        var identityAssembly = typeof(AppUser).Assembly;
        if (!MasterDbContext.ConfigurationAssemblies.Contains(identityAssembly))
            MasterDbContext.ConfigurationAssemblies.Add(identityAssembly);
    }

    public TenantCodeSeedPathTests()
        => _context = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _context.Dispose();

    /// <summary>
    /// Runs the real <see cref="IdentitySeeder.SeedAsync"/> over an InMemory master database.
    /// Tenants are seeded FIRST and committed before any later seed step, so the assertions
    /// below hold regardless of what the rest of the seeder does on this provider.
    /// </summary>
    private async Task SeedAsync(string configuredIdentifier)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenants:0:Id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ["Tenants:0:Identifier"] = configuredIdentifier,
                ["Tenants:0:Name"] = "Seeded Tenant",
                ["Tenants:0:ConnectionString"] = "Server=(local);Database=ERP_Seeded",
                ["Tenants:0:DbProvider"] = "SqlServer",
                ["Tenants:0:IsActive"] = "true",
                // Placeholder for a required configuration key, not a credential in use.
                ["SeedSettings:DefaultAdminPassword"] = "not-a-real-password",
            })
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Development");

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton<ILogger<MasterDbContext>>(NullLogger<MasterDbContext>.Instance);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(environment.Object);

        await using var provider = services.BuildServiceProvider();
        await IdentitySeeder.SeedAsync(provider);
    }

    [Theory]
    [InlineData("dev-tenant", "dev-tenant")]
    [InlineData("DEV-TENANT", "dev-tenant")]
    [InlineData("Dev-Tenant", "dev-tenant")]
    [InlineData("  dev-tenant  ", "dev-tenant")]
    public async Task SeedTenants_PersistsTheCanonicalCode(string configured, string expected)
    {
        await SeedAsync(configured);

        var tenant = await _context.Set<Tenant>().SingleAsync();
        Assert.Equal(expected, tenant.Code);
    }

    [Fact]
    public async Task SeedTenants_KeepsTheUnknownSentinelForAMissingIdentifier()
    {
        // Behaviour deliberately unchanged: a tenant entry with no Identifier still seeds the
        // pre-existing "unknown" sentinel. It is already canonical, so normalizing it is a
        // no-op — the fix does not quietly redefine what a misconfigured entry becomes.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenants:0:Id"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                ["Tenants:0:Name"] = "No Identifier",
                ["Tenants:0:ConnectionString"] = "Server=(local);Database=ERP_Seeded",
                ["SeedSettings:DefaultAdminPassword"] = "not-a-real-password",
            })
            .Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Development");

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton<ILogger<MasterDbContext>>(NullLogger<MasterDbContext>.Instance);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(environment.Object);

        await using var provider = services.BuildServiceProvider();
        await IdentitySeeder.SeedAsync(provider);

        var tenant = await _context.Set<Tenant>().SingleAsync();
        Assert.Equal("unknown", tenant.Code);
    }
}

/// <summary>
/// The TenantMiddleware cache key.
///
/// Exercises the middleware directly against a stubbed ITenantStore and a real MemoryCache,
/// mirroring <see cref="TenantMiddleware_TenantsPathResolutionTests"/>. These tests prove the
/// cache key is canonical AND that nothing else about the middleware moved — the header value
/// the store receives, the missing-header guard, and the JWT cross-check are all asserted
/// unchanged, because the approved scope was the cache key alone.
/// </summary>
public class TenantCodeCacheKeyTests
{
    private const string Header = "X-Tenant-Id";
    private const string CanonicalCode = "dev-tenant";
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<ITenantStore> _store = new(MockBehavior.Strict);
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private int _nextCalls;

    private TenantMiddleware Sut() => new(
        _ =>
        {
            _nextCalls++;
            return Task.CompletedTask;
        },
        NullLogger<TenantMiddleware>.Instance);

    private static TenantInfo Info(bool isActive = true) => new()
    {
        Id = TenantId,
        Identifier = CanonicalCode,
        Name = "Dev Tenant",
        ConnectionString = "Server=(local);Database=ERP_Dev",
        DbProvider = "SqlServer",
        IsActive = isActive,
    };

    private static DefaultHttpContext ContextFor(string? tenantHeader, string? tokenTenant = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tenants";
        context.Response.Body = new MemoryStream();

        if (tenantHeader is not null)
            context.Request.Headers[Header] = tenantHeader;

        if (tokenTenant is not null)
            context.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("tenant_id", tokenTenant)], authenticationType: "Test"));

        return context;
    }

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

    // ── The cache key is canonical ───────────────────────────────────────────

    [Fact]
    public async Task CacheEntry_IsKeyedOnTheCanonicalCode_NotTheRawHeader()
    {
        _store.Setup(s => s.GetByIdentifierAsync(It.IsAny<string>())).ReturnsAsync(Info());

        await Sut().InvokeAsync(ContextFor("DEV-TENANT"), _store.Object, _cache);

        Assert.True(_cache.TryGetValue($"tenant:{CanonicalCode}", out TenantInfo? cached));
        Assert.Equal(TenantId, cached!.Id);
        Assert.False(_cache.TryGetValue("tenant:DEV-TENANT", out TenantInfo? _));
    }

    [Theory]
    [InlineData("dev-tenant", "DEV-TENANT")]
    [InlineData("DEV-TENANT", "dev-tenant")]
    [InlineData("Dev-Tenant", "  dev-tenant  ")]
    public async Task DifferentCasingsOfOneTenant_ShareASingleCacheEntry(string first, string second)
    {
        // The defect this closes: keyed on the raw header, ONE tenant got a new cache entry
        // for every casing a client happened to send, and each new casing missed the cache
        // and hit the master database. Times.Once is what fails if the key is not canonical.
        _store.Setup(s => s.GetByIdentifierAsync(It.IsAny<string>())).ReturnsAsync(Info());

        var firstContext = ContextFor(first);
        var secondContext = ContextFor(second);

        await Sut().InvokeAsync(firstContext, _store.Object, _cache);
        await Sut().InvokeAsync(secondContext, _store.Object, _cache);

        _store.Verify(s => s.GetByIdentifierAsync(It.IsAny<string>()), Times.Once);
        Assert.Equal(2, _nextCalls);
        Assert.Same(firstContext.Items["CurrentTenant"], secondContext.Items["CurrentTenant"]);
    }

    [Fact]
    public async Task DistinctTenants_DoNotShareACacheEntry()
    {
        // Normalization must not collapse two different tenants onto one key.
        var other = new TenantInfo
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Identifier = "other-tenant",
            Name = "Other",
            ConnectionString = "Server=(local);Database=ERP_Other",
            DbProvider = "SqlServer",
            IsActive = true,
        };
        _store.Setup(s => s.GetByIdentifierAsync("DEV-TENANT")).ReturnsAsync(Info());
        _store.Setup(s => s.GetByIdentifierAsync("OTHER-TENANT")).ReturnsAsync(other);

        var a = ContextFor("DEV-TENANT");
        var b = ContextFor("OTHER-TENANT");

        await Sut().InvokeAsync(a, _store.Object, _cache);
        await Sut().InvokeAsync(b, _store.Object, _cache);

        _store.Verify(s => s.GetByIdentifierAsync(It.IsAny<string>()), Times.Exactly(2));
        Assert.Equal(TenantId, ((TenantInfo)a.Items["CurrentTenant"]!).Id);
        Assert.Equal(other.Id, ((TenantInfo)b.Items["CurrentTenant"]!).Id);
    }

    // ── Everything else about the middleware is unchanged ────────────────────

    [Fact]
    public async Task TheStoreStillReceivesTheRawHeaderValue()
    {
        // Scope evidence: only the CACHE KEY was normalized. Tenant resolution itself still
        // passes the header through exactly as sent, so this slice did not quietly change
        // what the store is asked for.
        _store.Setup(s => s.GetByIdentifierAsync("DEV-TENANT")).ReturnsAsync(Info());

        await Sut().InvokeAsync(ContextFor("DEV-TENANT"), _store.Object, _cache);

        _store.Verify(s => s.GetByIdentifierAsync("DEV-TENANT"), Times.Once);
        _store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnAbsentOrBlankHeaderIsStillRejectedBeforeAnyLookup(string? header)
    {
        // The missing-header guard runs BEFORE the cache key is built, so normalization never
        // gets the chance to turn whitespace into a lookup. No store call may occur.
        var context = ContextFor(header);

        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.Equal(0, _nextCalls);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("TENANT_HEADER_MISSING", CodeOf(context));
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnUnknownTenantStillReturns404()
    {
        _store.Setup(s => s.GetByIdentifierAsync("NO-SUCH-TENANT")).ReturnsAsync((TenantInfo?)null);

        var context = ContextFor("NO-SUCH-TENANT");
        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.Equal(0, _nextCalls);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("TENANT_NOT_FOUND", CodeOf(context));
        // A miss must not be cached under the canonical key either.
        Assert.False(_cache.TryGetValue("tenant:no-such-tenant", out TenantInfo? _));
    }

    [Fact]
    public async Task AnInactiveTenantStillReturns403()
    {
        _store.Setup(s => s.GetByIdentifierAsync("DEV-TENANT")).ReturnsAsync(Info(isActive: false));

        var context = ContextFor("DEV-TENANT");
        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.Equal(0, _nextCalls);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_INACTIVE", CodeOf(context));
    }

    [Theory]
    [InlineData("DEV-TENANT", "dev-tenant")]
    [InlineData("dev-tenant", "DEV-TENANT")]
    public async Task TheJwtCrossCheckRemainsCaseInsensitiveAndUntouched(string header, string tokenTenant)
    {
        // Already correct before this slice (StringComparison.OrdinalIgnoreCase) and left
        // exactly as it was. Asserted so a later refactor cannot tighten it into a
        // case-sensitive comparison on the assumption that codes are now canonical —
        // the token claim is not produced by this normalizer.
        _store.Setup(s => s.GetByIdentifierAsync(It.IsAny<string>())).ReturnsAsync(Info());

        var context = ContextFor(header, tokenTenant);
        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.Equal(1, _nextCalls);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.NotNull(context.Items["CurrentTenant"]);
    }

    [Fact]
    public async Task AGenuineTenantMismatchStillReturns403()
    {
        _store.Setup(s => s.GetByIdentifierAsync(It.IsAny<string>())).ReturnsAsync(Info());

        var context = ContextFor("DEV-TENANT", tokenTenant: "other-tenant");
        await Sut().InvokeAsync(context, _store.Object, _cache);

        Assert.Equal(0, _nextCalls);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("TENANT_MISMATCH", CodeOf(context));
    }
}
