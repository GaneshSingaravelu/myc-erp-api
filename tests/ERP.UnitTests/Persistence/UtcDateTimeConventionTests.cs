using System.Text.Json;
using ERP.Application.Abstractions.Identity;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.Persistence.Conversions;
using ERP.Persistence.Interceptors;
using ERP.Persistence.SqlServer.Extensions;
using ERP.Production.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;

namespace ERP.UnitTests.Persistence;

// ════════════════════════════════════════════════════════════════════════════════
// P1-h — every persisted DateTime is a UTC instant.
//
// THE DEFECT
// ==========
// SQL Server datetime2 stores no zone, so EF materialized every timestamp with
// DateTimeKind.Unspecified. The clock value was right (all writes are DateTime.UtcNow) but the
// Kind was a lie, and nothing complained, because > and < compare ticks and ignore Kind. The
// damage only surfaced where something CONVERTED:
//
//   * ToUniversalTime() on Unspecified applies the SERVER's offset — a silent multi-hour shift;
//   * JSON omitted the "Z", and JavaScript parses an offset-less date-time as LOCAL time, so
//     the client shifted it again;
//   * PostgreSQL's `timestamp with time zone` requires Kind=Utc and REJECTS Unspecified.
//
// APPROVED CONTRACTS
// ==================
//   business calendar date  ->  "2026-10-01"            (DateOnly, P1-g)
//   true UTC timestamp      ->  "2026-10-01T13:45:00Z"  (DateTime, this slice)
//
// Writes FAIL CLOSED on a non-UTC Kind. ToUniversalTime() and SpecifyKind-on-write were both
// rejected: the first guesses a time zone, the second relabels a lie as truth. Neither can be
// seen when it is wrong.
//
// EVIDENCE BOUNDARY: PostgreSQL is asserted at the MODEL level only. No PostgreSQL database was
// started and none is required for these tests; live PostgreSQL verification is P1-j/P1-k and is
// NOT claimed here.
// ════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// The write/read contract of the converter itself, in isolation — no database involved.
/// </summary>
public class UtcDateTimeContractTests
{
    private static readonly DateTime Clock = new(2026, 10, 1, 13, 45, 0);

    // ── writes: UTC accepted unchanged ──

    [Fact]
    public void Write_Utc_IsAcceptedUnchanged()
    {
        var utc = DateTime.SpecifyKind(Clock, DateTimeKind.Utc);

        var stored = UtcDateTime.ToStore(utc);

        Assert.Equal(utc.Ticks, stored.Ticks);
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
    }

    [Fact]
    public void Write_NullableUtc_IsAcceptedUnchanged()
    {
        DateTime? utc = DateTime.SpecifyKind(Clock, DateTimeKind.Utc);

        var stored = UtcDateTime.ToStoreNullable(utc);

        Assert.Equal(utc!.Value.Ticks, stored!.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, stored.Value.Kind);
    }

    // ── writes: everything else refused ──

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Write_NonUtc_IsRejected(DateTimeKind kind)
    {
        var value = DateTime.SpecifyKind(Clock, kind);

        var ex = Assert.Throws<InvalidOperationException>(() => UtcDateTime.ToStore(value));

        Assert.Contains($"Kind={kind}", ex.Message);
        Assert.Contains("UTC instant", ex.Message);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Write_NullableNonUtc_IsRejected(DateTimeKind kind)
    {
        DateTime? value = DateTime.SpecifyKind(Clock, kind);

        Assert.Throws<InvalidOperationException>(() => UtcDateTime.ToStoreNullable(value));
    }

    [Fact]
    public void Write_NonUtc_IsNotSilentlyShifted()
    {
        // THE control for the rejected alternative. ToUniversalTime() on an Unspecified value
        // applies the machine's offset, which is how an instant moves by hours without anyone
        // noticing. The guard must refuse rather than produce that value.
        var unspecified = DateTime.SpecifyKind(Clock, DateTimeKind.Unspecified);
        var whatToUniversalTimeWouldHaveDone = unspecified.ToUniversalTime();

        var ex = Record.Exception(() => UtcDateTime.ToStore(unspecified));

        Assert.IsType<InvalidOperationException>(ex);
        // Guard against a future "helpful" rewrite: if the offset here is non-zero, the shifted
        // value is demonstrably different, and the guard is what stops it being persisted.
        if (TimeZoneInfo.Local.GetUtcOffset(unspecified) != TimeSpan.Zero)
            Assert.NotEqual(unspecified.Ticks, whatToUniversalTimeWouldHaveDone.Ticks);
    }

    [Fact]
    public void Write_Null_StaysNull()
    {
        Assert.Null(UtcDateTime.ToStoreNullable(null));
    }

    // ── reads: Kind restored, ticks untouched ──

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    public void Read_RestoresUtcKind_WithoutChangingTicks(DateTimeKind storedKind)
    {
        var fromStore = DateTime.SpecifyKind(Clock, storedKind);

        var materialized = UtcDateTime.FromStore(fromStore);

        Assert.Equal(DateTimeKind.Utc, materialized.Kind);
        Assert.Equal(Clock.Ticks, materialized.Ticks);
    }

    [Fact]
    public void Read_Nullable_NullStaysNull_NonNullGetsUtc()
    {
        Assert.Null(UtcDateTime.FromStoreNullable(null));

        var materialized = UtcDateTime.FromStoreNullable(DateTime.SpecifyKind(Clock, DateTimeKind.Unspecified));

        Assert.Equal(DateTimeKind.Utc, materialized!.Value.Kind);
        Assert.Equal(Clock.Ticks, materialized.Value.Ticks);
    }
}

/// <summary>
/// The convention is applied to BOTH contexts and covers the WHOLE persisted timestamp
/// inventory on BOTH providers, while leaving <c>DateOnly</c> alone.
/// </summary>
public class UtcDateTimeModelTests
{
    // No per-call registration helper here on purpose. TestEntityConfigurationRegistration's
    // [ModuleInitializer] has already populated both ConfigurationAssemblies lists before any
    // test runs, so registering again at the point of use would add nothing and would reintroduce
    // writes to a shared list during the run.

    // Connection strings are never opened — model building is offline. The provider seam still
    // matters, because store types only exist once a relational provider is configured.
    private const string SqlServerCs = "Server=unused;Database=model-only;Trusted_Connection=True;";
    private const string NpgsqlCs = "Host=unused;Database=model-only;Username=u;Password=p";

    // EF's DEFAULT model cache is used deliberately — no ReplaceService<IModelCacheKeyFactory>.
    //
    // An earlier revision of these tests replaced the model-cache key with a per-instance Guid to
    // dodge a race in which a model could be built before the Production assembly was registered.
    // That was the wrong remedy and measurably harmful: a unique key rebuilds the model for every
    // context instance, and ReplaceService forces a separate EF internal service provider, so each
    // of the ~13 model builds here re-entered OnModelCreating and re-enumerated the shared
    // ConfigurationAssemblies list. Measured over 10 full-suite runs: 8/10 with that factory,
    // 10/10 without it, everything else unchanged.
    //
    // The race it was working around is now fixed at its source by
    // TestEntityConfigurationRegistration, whose [ModuleInitializer] populates both lists once
    // before any test runs. Registration is therefore complete before the first model build and
    // the default cache is safe.
    private static IModel MasterModel(bool npgsql)
    {
        var b = new DbContextOptionsBuilder<MasterDbContext>();
        if (npgsql) b.UseNpgsql(NpgsqlCs); else b.UseSqlServerProvider(SqlServerCs);
        using var context = new MasterDbContext(b.Options);
        return context.Model;
    }

    private static IModel AppModel(bool npgsql)
    {
        var b = new DbContextOptionsBuilder<AppDbContext>();
        if (npgsql) b.UseNpgsql(NpgsqlCs); else b.UseSqlServerProvider(SqlServerCs);
        using var context = new AppDbContext(b.Options);
        return context.Model;
    }

    private static List<IProperty> TimestampProperties(IModel model) =>
        [.. model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?))];

    private static string Describe(IProperty p) => $"{p.DeclaringType.ClrType.Name}.{p.Name}";

    // ── coverage: EVERY timestamp, not one representative ──

    [Theory]
    [InlineData(false)] // SQL Server
    [InlineData(true)]  // PostgreSQL
    public void MasterDb_EveryTimestamp_HasTheUtcConverter(bool npgsql)
    {
        var properties = TimestampProperties(MasterModel(npgsql));

        // A convention that silently covered nothing would make every other assertion vacuous.
        Assert.NotEmpty(properties);

        var uncovered = properties
            .Where(p => p.GetValueConverter() is not (UtcDateTimeConverter or UtcNullableDateTimeConverter))
            .Select(Describe)
            .ToList();

        Assert.Empty(uncovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AppDb_EveryTimestamp_HasTheUtcConverter(bool npgsql)
    {
        var properties = TimestampProperties(AppModel(npgsql));

        Assert.NotEmpty(properties);

        var uncovered = properties
            .Where(p => p.GetValueConverter() is not (UtcDateTimeConverter or UtcNullableDateTimeConverter))
            .Select(Describe)
            .ToList();

        Assert.Empty(uncovered);
    }

    [Fact]
    public void TheCoveredInventoryIsTheWholeInventory_AndIsNotTrivial()
    {
        // Pins SCALE as well as the property-by-property check: if a future change dropped most
        // of the model from the convention, "every covered property is covered" could still pass
        // on a handful of properties.
        //
        // Deliberately a LOWER BOUND rather than an exact count. Both contexts discover entities
        // through a shared static ConfigurationAssemblies list that other test classes mutate
        // concurrently, so the exact property count observed here is not deterministic under
        // parallel execution — an exact assertion measured that race, not this convention, and
        // failed intermittently. The bound is far above "a handful" and well below the counts
        // recorded at the P1-h baseline (Master 26, AppDb 13), so it still fails loudly if
        // coverage collapses, without being flaky.
        var master = TimestampProperties(MasterModel(npgsql: false));
        var app = TimestampProperties(AppModel(npgsql: false));

        Assert.True(master.Count >= 20, $"MasterDb timestamp properties: {master.Count}");
        Assert.True(app.Count >= 10, $"AppDb timestamp properties: {app.Count}");

        Assert.All(master, p => Assert.NotNull(p.GetValueConverter()));
        Assert.All(app, p => Assert.NotNull(p.GetValueConverter()));

        // Named anchors, so an incomplete model fails here rather than passing vacuously.
        Assert.Contains(master, p => p.DeclaringType.ClrType == typeof(UserSession) && p.Name == nameof(UserSession.ExpiresAt));
        Assert.Contains(app, p => p.DeclaringType.ClrType == typeof(WorkOrder) && p.Name == nameof(WorkOrder.CreatedAt));
    }

    [Fact]
    public void NullableAndNonNullable_GetTheMatchingConverter()
    {
        var entity = MasterModel(npgsql: false).FindEntityType(typeof(UserSession))!;

        Assert.IsType<UtcDateTimeConverter>(entity.FindProperty(nameof(UserSession.CreatedAt))!.GetValueConverter());
        Assert.IsType<UtcNullableDateTimeConverter>(entity.FindProperty(nameof(UserSession.RevokedAt))!.GetValueConverter());
    }

    // ── the migration-free gate: store types must not move ──

    [Fact]
    public void MasterDb_SqlServer_TimestampsRemainDatetime2()
    {
        var wrong = TimestampProperties(MasterModel(npgsql: false))
            .Where(p => p.GetColumnType() != "datetime2")
            .Select(p => $"{Describe(p)}={p.GetColumnType()}")
            .ToList();

        Assert.Empty(wrong);
    }

    [Fact]
    public void AppDb_SqlServer_TimestampsRemainDatetime2()
    {
        var wrong = TimestampProperties(AppModel(npgsql: false))
            .Where(p => p.GetColumnType() != "datetime2")
            .Select(p => $"{Describe(p)}={p.GetColumnType()}")
            .ToList();

        Assert.Empty(wrong);
    }

    [Fact]
    public void MasterDb_PostgreSql_TimestampsRemainTimestampWithTimeZone()
    {
        // MODEL-level evidence only. No PostgreSQL server is involved and none is needed to
        // assert the store type EF would generate.
        var wrong = TimestampProperties(MasterModel(npgsql: true))
            .Where(p => p.GetColumnType() != "timestamp with time zone")
            .Select(p => $"{Describe(p)}={p.GetColumnType()}")
            .ToList();

        Assert.Empty(wrong);
    }

    [Fact]
    public void AppDb_PostgreSql_TimestampsRemainTimestampWithTimeZone()
    {
        var wrong = TimestampProperties(AppModel(npgsql: true))
            .Where(p => p.GetColumnType() != "timestamp with time zone")
            .Select(p => $"{Describe(p)}={p.GetColumnType()}")
            .ToList();

        Assert.Empty(wrong);
    }

    // ── P1-g must be untouched ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkOrderDueDate_StaysDateOnly_Date_AndUnconverted(bool npgsql)
    {
        // A DateTime convention must not reach DateOnly. If it ever did, P1-g's calendar-date
        // semantics would silently regress to a timestamp.
        var property = AppModel(npgsql).FindEntityType(typeof(WorkOrder))!
            .FindProperty(nameof(WorkOrder.DueDate))!;

        Assert.Equal(typeof(DateOnly?), property.ClrType);
        Assert.Equal("date", property.GetColumnType());
        Assert.Null(property.GetValueConverter());
    }

    [Fact]
    public void NoOtherClrTypeWasCaughtByTheConvention()
    {
        // Scope evidence: the convention must not have attached a UTC converter to anything
        // that is not a DateTime — strings, Guids, enums, DateOnly.
        var strayed = AppModel(npgsql: false).GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType != typeof(DateTime) && p.ClrType != typeof(DateTime?))
            .Where(p => p.GetValueConverter() is UtcDateTimeConverter or UtcNullableDateTimeConverter)
            .Select(Describe)
            .ToList();

        Assert.Empty(strayed);
    }

    // ── query translation: no client evaluation introduced ──

    [Fact]
    public void SessionExpiryPredicate_StillTranslatesServerSide()
    {
        var nowUtc = DateTime.UtcNow;
        var b = new DbContextOptionsBuilder<MasterDbContext>();
        b.UseSqlServerProvider(SqlServerCs);
        using var context = new MasterDbContext(b.Options);

        // Mirrors UserSessionRepository's active-session lookup.
        var sql = context.Set<UserSession>()
            .Where(s => !s.IsRevoked && s.RefreshTokenExpiry > nowUtc && s.ExpiresAt > nowUtc)
            .ToQueryString();

        Assert.Contains("[RefreshTokenExpiry] >", sql);
        Assert.Contains("[ExpiresAt] >", sql);
        Assert.Contains("FROM [UserSessions]", sql);
    }

    [Fact]
    public void NullableLockoutComparison_StillTranslatesServerSide()
    {
        var nowUtc = DateTime.UtcNow;
        var b = new DbContextOptionsBuilder<MasterDbContext>();
        b.UseSqlServerProvider(SqlServerCs);
        using var context = new MasterDbContext(b.Options);

        var sql = context.Set<AppUser>().Where(u => u.LockoutEnd > nowUtc).ToQueryString();

        Assert.Contains("[LockoutEnd] >", sql);
        Assert.Contains("FROM [Users]", sql);
    }

    [Fact]
    public void PasswordResetExpiryComparison_StillTranslatesServerSide()
    {
        var nowUtc = DateTime.UtcNow;
        var b = new DbContextOptionsBuilder<MasterDbContext>();
        b.UseSqlServerProvider(SqlServerCs);
        using var context = new MasterDbContext(b.Options);

        var sql = context.Set<AppUser>().Where(u => u.PasswordResetTokenExpiry < nowUtc).ToQueryString();

        Assert.Contains("[PasswordResetTokenExpiry] <", sql);
    }
}

/// <summary>
/// The approved JSON contract, asserted so it is intentional rather than incidental.
/// No custom converter exists or is wanted — this is native System.Text.Json behaviour.
/// </summary>
public class UtcDateTimeJsonContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private record TimestampPayload(DateTime CreatedAt, DateTime? LastLoginAt);

    [Fact]
    public void UtcTimestamp_SerializesWithExplicitZ()
    {
        var utc = DateTime.SpecifyKind(new DateTime(2026, 10, 1, 13, 45, 0), DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(new TimestampPayload(utc, utc), Web);

        using var document = JsonDocument.Parse(json);
        var createdAt = document.RootElement.GetProperty("createdAt").GetString();
        var lastLoginAt = document.RootElement.GetProperty("lastLoginAt").GetString();

        // Owner decision (option A): an instant on the wire states its zone. An offset-less
        // date-time is ambiguous, and JavaScript parses it as LOCAL time — which is how a
        // correct UTC value gets displayed hours out.
        Assert.Equal("2026-10-01T13:45:00Z", createdAt);
        Assert.Equal("2026-10-01T13:45:00Z", lastLoginAt);
        Assert.EndsWith("Z", createdAt!);
    }

    [Fact]
    public void NullTimestamp_SerializesAsNull()
    {
        var utc = DateTime.SpecifyKind(new DateTime(2026, 10, 1, 13, 45, 0), DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(new TimestampPayload(utc, null), Web);

        Assert.Contains("\"lastLoginAt\":null", json);
    }

    [Fact]
    public void CalendarDate_RemainsDateOnly_WithNoZ()
    {
        // P1-g regression guard: the two contracts must stay distinguishable on the wire.
        var json = JsonSerializer.Serialize(new { dueDate = new DateOnly(2026, 10, 1) }, Web);

        using var document = JsonDocument.Parse(json);
        var dueDate = document.RootElement.GetProperty("dueDate").GetString();

        Assert.Equal("2026-10-01", dueDate);
        Assert.DoesNotContain("Z", dueDate!);
        Assert.DoesNotContain("T", dueDate!);
    }

    [Fact]
    public void NoCustomJsonConverterIsRequired()
    {
        // The Z comes from the Kind, not from a serialization workaround. Default options, no
        // registered converters: if someone later "fixes" this with a converter, this fails.
        Assert.Empty(Web.Converters);

        var utc = DateTime.SpecifyKind(new DateTime(2026, 10, 1, 13, 45, 0), DateTimeKind.Utc);
        Assert.Equal("\"2026-10-01T13:45:00Z\"", JsonSerializer.Serialize(utc, Web));
    }
}

/// <summary>
/// Relational round-trip on SQLite — a real database boundary, in-process. Proves the converter
/// survives materialization. SQL Server's own round-trip is verified separately against a
/// throwaway database.
/// </summary>
public class UtcDateTimeRelationalTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    static UtcDateTimeRelationalTests()
    {
        var production = typeof(ERP.Production.Infrastructure.Configurations.WorkOrderConfiguration).Assembly;
        if (!AppDbContext.ConfigurationAssemblies.Contains(production))
            AppDbContext.ConfigurationAssemblies.Add(production);
    }

    public UtcDateTimeRelationalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // The AuditableEntityInterceptor is what sets CreatedAt/LastModifiedAt/DeletedAt to
        // DateTime.UtcNow, and production registers it on BOTH context paths
        // (InfrastructureServiceExtensions for Master, TenantDbContextFactory per tenant). It is
        // wired here too, because without it CreatedAt stays default(DateTime) — Kind=Unspecified
        // — and the UTC guard correctly refuses the save. A fixture that omitted it would be
        // testing a context production never builds.
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());
        currentUser.SetupGet(c => c.TenantId).Returns((string?)null);

        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditableEntityInterceptor(currentUser.Object))
            .Options);

        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static WorkOrder NewWorkOrder(string orderNumber) => new(
        orderNumber, "Steel Rod Type A", 10, Guid.NewGuid(), Guid.NewGuid(), dueDate: null);

    [Fact]
    public async Task UtcTimestamp_RoundTrips_WithKindUtcAndUnchangedTicks()
    {
        var wo = NewWorkOrder("WO-UTC-RT");
        _context.Set<WorkOrder>().Add(wo);
        await _context.SaveChangesAsync();   // CreatedAt is set by the audit interceptor / default

        var written = wo.CreatedAt;
        _context.ChangeTracker.Clear();

        var reloaded = await _context.Set<WorkOrder>().SingleAsync(x => x.OrderNumber == "WO-UTC-RT");

        Assert.Equal(DateTimeKind.Utc, reloaded.CreatedAt.Kind);
        Assert.Equal(written.Ticks, reloaded.CreatedAt.Ticks);
    }

    [Fact]
    public async Task NullableTimestamp_NullRoundTrips_ThenUtcRoundTrips()
    {
        var wo = NewWorkOrder("WO-UTC-NULLABLE");
        _context.Set<WorkOrder>().Add(wo);
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        Assert.Null((await _context.Set<WorkOrder>().SingleAsync(x => x.OrderNumber == "WO-UTC-NULLABLE")).ReleasedAt);

        var tracked = await _context.Set<WorkOrder>().SingleAsync(x => x.OrderNumber == "WO-UTC-NULLABLE");
        tracked.Release();   // sets ReleasedAt = DateTime.UtcNow
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var released = (await _context.Set<WorkOrder>().SingleAsync(x => x.OrderNumber == "WO-UTC-NULLABLE")).ReleasedAt;

        Assert.NotNull(released);
        Assert.Equal(DateTimeKind.Utc, released.Value.Kind);
    }

    // WHERE THE WRITE GUARD ACTUALLY BITES — and why no test lives here for it.
    //
    // AuditableEntityInterceptor assigns CreatedAt/LastModifiedAt/DeletedAt = DateTime.UtcNow on
    // every save, so a non-UTC value written to one of those audit fields is REPLACED before the
    // converter ever sees it. The guard therefore bites on the NON-audit timestamps —
    // ExpiresAt, RefreshTokenExpiry, LockoutEnd, LastAccessedAt, ReleasedAt — which no interceptor
    // rewrites. Those are covered by
    // UtcDateTimeMasterRelationalTests.NonUtcWrite_IsRejectedBeforeItReachesTheStore (Unspecified
    // and Local), by the UtcDateTimeContractTests rejection theories, and by live SQL Server.
    //
    // A test asserting the interceptor's overwrite used to sit here and was removed. Its subject
    // was AuditableEntityInterceptor ordering: behaviour that predates P1-h, that P1-h does not
    // modify, and whose registration is covered independently (MasterProviderSelectionTests,
    // AuditLogInterceptorTests, and three Identity test bases). It asserted the ABSENCE of any
    // exception across a SaveChangesAsync, which made it the one test here that turned any
    // transient failure under full-suite parallelism into a P1-h failure — it failed 1 run in 20
    // while passing 25/25 alone, 20/20 in its class and 15/15 across the P1-h suite. It never
    // asserted the UTC contract, so removing it costs no P1-h coverage.
}

/// <summary>
/// Master-database relational round-trip on SQLite, used for the timestamps NO interceptor
/// rewrites — which is where the write guard actually bites.
/// </summary>
public class UtcDateTimeMasterRelationalTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MasterDbContext _context;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();

    static UtcDateTimeMasterRelationalTests()
    {
        var identity = typeof(AppUser).Assembly;
        if (!MasterDbContext.ConfigurationAssemblies.Contains(identity))
            MasterDbContext.ConfigurationAssemblies.Add(identity);
    }

    public UtcDateTimeMasterRelationalTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(c => c.UserId).Returns(Guid.NewGuid().ToString());
        currentUser.SetupGet(c => c.TenantId).Returns((string?)null);

        _context = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditableEntityInterceptor(currentUser.Object))
            .Options);

        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<UserSession> SeedSessionAsync()
    {
        var utcNow = DateTime.UtcNow;

        _context.Set<Tenant>().Add(new Tenant
        {
            Id = _tenantId, Code = "utc-probe", Name = "Utc Probe",
            ConnectionString = "DataSource=:memory:", DbProvider = "SqlServer",
            IsActive = true, CreatedAt = utcNow,
        });
        _context.Set<AppUser>().Add(new AppUser
        {
            Id = _userId, UserName = "utcprobe", NormalizedUserName = "UTCPROBE",
            Email = "utc@probe.local", NormalizedEmail = "UTC@PROBE.LOCAL",
            PasswordHash = "x", SecurityStamp = Guid.NewGuid().ToString(),
            FirstName = "Utc", LastName = "Probe", IsActive = true,
        });
        var session = new UserSession
        {
            UserId = _userId, TenantId = _tenantId,
            RefreshTokenHash = "rt", AccessTokenJti = "jti",
            RefreshTokenExpiry = utcNow.AddDays(7), ExpiresAt = utcNow.AddDays(7), CreatedAt = utcNow,
        };
        _context.Set<UserSession>().Add(session);
        await _context.SaveChangesAsync();
        return session;
    }

    [Fact]
    public async Task NonAuditTimestamp_Utc_RoundTripsWithKindUtcAndUnchangedTicks()
    {
        var session = await SeedSessionAsync();
        var writtenExpiry = session.ExpiresAt;

        _context.ChangeTracker.Clear();
        var reloaded = await _context.Set<UserSession>().SingleAsync(s => s.UserId == _userId);

        Assert.Equal(DateTimeKind.Utc, reloaded.ExpiresAt.Kind);
        Assert.Equal(writtenExpiry.Ticks, reloaded.ExpiresAt.Ticks);
        Assert.Equal(DateTimeKind.Utc, reloaded.RefreshTokenExpiry.Kind);
    }

    [Fact]
    public async Task NonAuditNullableTimestamp_NullThenUtc_RoundTrips()
    {
        var session = await SeedSessionAsync();

        _context.ChangeTracker.Clear();
        Assert.Null((await _context.Set<UserSession>().SingleAsync(s => s.UserId == _userId)).LastAccessedAt);

        var tracked = await _context.Set<UserSession>().SingleAsync(s => s.UserId == _userId);
        var utc = DateTime.UtcNow;
        tracked.LastAccessedAt = utc;
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var reloaded = await _context.Set<UserSession>().SingleAsync(s => s.UserId == _userId);

        Assert.NotNull(reloaded.LastAccessedAt);
        Assert.Equal(DateTimeKind.Utc, reloaded.LastAccessedAt.Value.Kind);
        Assert.Equal(utc.Ticks, reloaded.LastAccessedAt.Value.Ticks);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public async Task NonUtcWrite_IsRejectedBeforeItReachesTheStore(DateTimeKind kind)
    {
        await SeedSessionAsync();
        var offending = DateTime.SpecifyKind(new DateTime(2026, 6, 1, 12, 0, 0), kind);

        var tracked = await _context.Set<UserSession>().SingleAsync(s => s.UserId == _userId);
        tracked.LastAccessedAt = offending;   // no interceptor rewrites this field

        var ex = await Record.ExceptionAsync(() => _context.SaveChangesAsync());

        Assert.NotNull(ex);
        // EF wraps store failures, so walk the chain rather than asserting the outer type.
        var chain = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException) chain.Add(e);
        Assert.Contains(chain, e => e is InvalidOperationException && e.Message.Contains($"Kind={kind}"));

        // And nothing was persisted — the value never reached the store, shifted or otherwise.
        _context.ChangeTracker.Clear();
        Assert.Null((await _context.Set<UserSession>().SingleAsync(s => s.UserId == _userId)).LastAccessedAt);
    }
}
