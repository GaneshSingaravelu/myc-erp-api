namespace ERP.ParityCheck;

// ── Measurements: what a probe observed. Live probes read them from a real database; the
//    offline simulation supplies canned values. Classification never depends on the source.

public sealed record SchemaMeasurement(
    string[] Tables, int Columns, IReadOnlyDictionary<string, int> ColumnCategories,
    int Indexes, int UniqueIndexes, int FilteredIndexes, int ForeignKeys);

public sealed record PrecisionMeasurement(
    long AlignedWrittenTicks, long AlignedReadTicks, long SubMicrosecondWrittenTicks, long SubMicrosecondReadTicks,
    DateTimeKind ReadKind);

public sealed record DateOnlyMeasurement(string MinRaw, DateOnly? MinRead, string MaxRaw, DateOnly? MaxRead);

public sealed record UtcMeasurement(
    bool UtcRoundTripExact, DateTimeKind UtcReadKind,
    bool LocalRejectedByApplicationGuard, bool LocalRowPersisted,
    bool UnspecifiedRejectedByApplicationGuard, bool UnspecifiedRowPersisted,
    bool NullableNullRoundTrip, string Notes);

public sealed record NullableUniqueMeasurement(bool NullTenantDuplicatesAccepted, bool SameTenantDuplicateRejected, string Notes);

public sealed record ForeignKeyMeasurement(
    bool OrphanInsertRejected, bool CascadeRemovedDependents, bool RestrictBlockedPrincipalDelete,
    bool RestrictPrincipalSurvived, string Notes);

/// <summary>
/// L-8 observations. <see cref="RawCaseVariantMatches"/> and <see cref="CaseVariantDuplicateAccepted"/>
/// are MEASURED on the real column, and are what classification uses. <see cref="Collation"/>
/// (server/database/column collation names) and <see cref="LiteralComparisonCaseInsensitive"/>
/// (the database default compared on two literals) are recorded evidence only (D-k11).
/// </summary>
public sealed record CodeCaseMeasurement(
    string Entity, bool? ApplicationLookupResolvesAnyCase, bool RawCaseVariantMatches, bool CaseVariantDuplicateAccepted,
    string? Collation = null, bool? LiteralComparisonCaseInsensitive = null);

/// <summary>
/// L-9 observations. <see cref="SeederErrors"/> holds Error/Critical entries IdentitySeeder
/// logged (it swallows its own exceptions) — already redacted and bounded by the capturing logger.
/// </summary>
public sealed record SeedMeasurement(
    IReadOnlyDictionary<string, int> Counts, string Fingerprint, bool SecondRunChangedNothing,
    IReadOnlyList<string>? SeederErrors = null);

public sealed record DownUpMeasurement(int TablesAfterDown, int HistoryRowsAfterDown, int TablesAfterUp, string[] HistoryAfterUp);

/// <summary>
/// L-10 (Master) — the Company objects as the catalog reports them at one stage.
/// <see cref="ForeignKeyDeleteAction"/> is the catalog spelling (SQL Server
/// <c>delete_referential_action_desc</c>; PostgreSQL <c>confdeltype</c> mapped to a word), or null.
/// </summary>
public sealed record CompanyShape(
    bool CompaniesTable, string[] CompanyColumns, string[] NullableCompanyColumns,
    bool TenantCompanyIdColumn, bool TenantCompanyIdNullable,
    bool? CodeIndexUnique, bool TenantCompanyIdIndex, string? ForeignKeyDeleteAction)
{
    public static readonly CompanyShape Absent = new(false, [], [], false, false, null, false, null);
}

/// <summary>
/// One L-10 stage. <see cref="Fingerprints"/> maps each fixture table to a SHA-256 of its
/// ORIGINAL (pre-upgrade) columns, canonicalized provider-neutrally. <see cref="CompaniesRows"/>
/// and <see cref="TenantsWithCompany"/> are -1 when the objects they count do not exist.
/// </summary>
public sealed record UpgradeStage(
    string Name, string[] History, SchemaMeasurement Schema, CompanyShape Shape,
    IReadOnlyDictionary<string, string> Fingerprints, long CompaniesRows, long TenantsWithCompany);

/// <summary>
/// A constraint probe run inside a transaction that was rolled back. <see cref="RolledBack"/> is
/// true only when the rollback was observed afterwards from a separate connection.
/// </summary>
public sealed record ProbeResult(string Name, bool Rejected, bool RolledBack, string Detail);

/// <summary>
/// Everything one live Master L-10 cycle observed. <see cref="BeforeRowCounts"/> holds the
/// rows actually persisted per fixture table at the Phase 1 head, counted from the database —
/// not the number of INSERT statements executed (<see cref="FixtureRows"/>).
/// </summary>
public sealed record UpgradeMeasurement(
    UpgradeStage Before, UpgradeStage AfterUp, UpgradeStage AfterDown, UpgradeStage AfterReUp,
    IReadOnlyList<ProbeResult> Probes, int FixtureRows, IReadOnlyDictionary<string, long> BeforeRowCounts);

/// <summary>The live check matrix: identity, title and which context each check runs in.</summary>
public static class Checks
{
    public const string L1 = "L-1", L2 = "L-2", L3 = "L-3", L4 = "L-4", L5 = "L-5", L6 = "L-6",
        L7 = "L-7", L8 = "L-8", L9 = "L-9", L10 = "L-10", L11 = "L-11";

    public static readonly (string Id, string Title, DbContextKind[] Contexts)[] PerProvider =
    [
        (L1, "migrate empty database; verify schema", [DbContextKind.Master, DbContextKind.AppDb]),
        (L2, "__EFMigrationsHistory equals provider lineage", [DbContextKind.Master, DbContextKind.AppDb]),
        (L9, "IdentitySeeder: non-secret structure, idempotent", [DbContextKind.Master]),
        (L3, "timestamp precision", [DbContextKind.Master]),
        (L4, "DateOnly MinValue/MaxValue storage", [DbContextKind.AppDb]),
        (L5, "UTC DateTime persistence contract", [DbContextKind.Master, DbContextKind.AppDb]),
        (L6, "nullable unique index (IX_Roles_NormalizedName_TenantId)", [DbContextKind.Master]),
        (L7, "foreign keys: enforcement, Cascade, Restrict", [DbContextKind.Master, DbContextKind.AppDb]),
        (L8, "Code case-sensitivity (Tenant.Code / Plant.Code)", [DbContextKind.Master, DbContextKind.AppDb]),
        (L11, "migration Down/Up", [DbContextKind.Master, DbContextKind.AppDb]),
        // Master only: AppDb has no incremental PostgreSQL migration, so its L-10 is reported
        // once, as DEFERRED (Evaluator.DeferredAppDb), never per provider.
        (L10, "incremental upgrade path", [DbContextKind.Master]),
    ];

    public static string Title(string id) =>
        id == L10 ? "incremental upgrade path" : id == L9 + "x" ? "IdentitySeeder cross-provider equivalence"
        : PerProvider.Single(c => c.Id == id).Title;

    public static string Scope(Provider p, DbContextKind k) => $"{p}/{k}";

    public static IEnumerable<(string Id, DbContextKind Kind)> For(DbContextKind kind) =>
        PerProvider.Where(c => c.Contexts.Contains(kind)).Select(c => (c.Id, kind));
}

/// <summary>
/// PURE CLASSIFICATION. Every rule here is an independent expectation; nothing passes merely
/// because two providers agree with each other.
/// </summary>
public static class Evaluator
{
    private static CheckResult R(string id, Provider p, DbContextKind k, Classification c, string detail) =>
        new(id, Checks.Title(id), Checks.Scope(p, k), c, detail);

    public static CheckResult Schema(Provider p, DbContextKind k, SchemaMeasurement m, ContextExpectations e)
    {
        var problems = new List<string>();
        var expectedTables = e.Tables.Order(StringComparer.Ordinal).ToArray();
        var actualTables = m.Tables.Order(StringComparer.Ordinal).ToArray();
        if (!expectedTables.SequenceEqual(actualTables))
            problems.Add($"tables differ: missing [{string.Join(",", expectedTables.Except(actualTables))}] unexpected [{string.Join(",", actualTables.Except(expectedTables))}]");
        if (m.Columns != e.Columns) problems.Add($"columns {m.Columns} != {e.Columns}");
        foreach (var (category, expected) in e.ColumnCategories)
        {
            var actual = m.ColumnCategories.GetValueOrDefault(category);
            if (actual != expected) problems.Add($"{category} columns {actual} != {expected}");
        }
        foreach (var unknown in m.ColumnCategories.Keys.Except(e.ColumnCategories.Keys))
            problems.Add($"unapproved column type '{unknown}' x{m.ColumnCategories[unknown]}");
        if (m.Indexes != e.Indexes) problems.Add($"indexes {m.Indexes} != {e.Indexes}");
        if (m.UniqueIndexes != e.UniqueIndexes) problems.Add($"unique indexes {m.UniqueIndexes} != {e.UniqueIndexes}");
        if (m.FilteredIndexes != e.FilteredIndexes(p)) problems.Add($"filtered indexes {m.FilteredIndexes} != {e.FilteredIndexes(p)}");
        if (m.ForeignKeys != e.ForeignKeys) problems.Add($"foreign keys {m.ForeignKeys} != {e.ForeignKeys}");

        return problems.Count == 0
            ? R(Checks.L1, p, k, Classification.Pass,
                $"{m.Tables.Length} tables, {m.Columns} columns, {m.Indexes} indexes ({m.UniqueIndexes} unique, {m.FilteredIndexes} filtered), {m.ForeignKeys} FKs")
            : R(Checks.L1, p, k, Classification.FailDefect, string.Join("; ", problems));
    }

    public static CheckResult History(Provider p, DbContextKind k, string[] actual, ContextExpectations e)
    {
        var expected = e.Lineage(p);
        return actual.SequenceEqual(expected)
            ? R(Checks.L2, p, k, Classification.Pass, $"[{string.Join(", ", actual)}]")
            : R(Checks.L2, p, k, Classification.FailDefect, $"expected [{string.Join(", ", expected)}], found [{string.Join(", ", actual)}]");
    }

    /// <summary>
    /// SQL Server datetime2(7) keeps 100 ns; PostgreSQL keeps 1 µs (approved difference).
    /// A µs-aligned value must round-trip exactly on both, and must come back as UTC.
    /// </summary>
    public static CheckResult Precision(Provider p, DbContextKind k, PrecisionMeasurement m)
    {
        if (m.ReadKind != DateTimeKind.Utc)
            return R(Checks.L3, p, k, Classification.FailDefect, $"materialized Kind={m.ReadKind}, expected Utc");
        if (m.AlignedReadTicks != m.AlignedWrittenTicks)
            return R(Checks.L3, p, k, Classification.FailDefect, $"µs-aligned value changed: {m.AlignedWrittenTicks} -> {m.AlignedReadTicks}");

        var delta = m.SubMicrosecondWrittenTicks - m.SubMicrosecondReadTicks;
        if (p == Provider.SqlServer)
            return delta == 0
                ? R(Checks.L3, p, k, Classification.Pass, "datetime2(7): 100 ns ticks preserved exactly")
                : R(Checks.L3, p, k, Classification.FailDefect, $"datetime2 lost precision: Δ={delta} ticks");

        return delta != 0 && Math.Abs(delta) < 10 && m.SubMicrosecondReadTicks % 10 == 0
            ? R(Checks.L3, p, k, Classification.ExpectedDifference, $"timestamptz keeps microseconds: sub-µs ticks dropped (Δ={delta} ticks); µs-aligned exact")
            : delta == 0
                ? R(Checks.L3, p, k, Classification.FailDefect, "sub-µs ticks preserved — contradicts the approved microsecond-precision expectation")
                : R(Checks.L3, p, k, Classification.FailDefect, $"unexpected precision loss Δ={delta} ticks");
    }

    /// <summary>SQL Server stores ordinary min/max dates; PostgreSQL may store ±infinity (approved).</summary>
    public static CheckResult DateOnlyExtremes(Provider p, DbContextKind k, DateOnlyMeasurement m)
    {
        if (m.MinRead != DateOnly.MinValue || m.MaxRead != DateOnly.MaxValue)
            return R(Checks.L4, p, k, Classification.FailDefect, $"round-trip changed value: min->{m.MinRead}, max->{m.MaxRead}");

        var ordinary = m.MinRaw == "0001-01-01" && m.MaxRaw == "9999-12-31";
        var infinity = m.MinRaw == "-infinity" && m.MaxRaw == "infinity";
        var stored = $"stored '{m.MinRaw}' / '{m.MaxRaw}', round-trip exact";

        return (p, ordinary, infinity) switch
        {
            (_, true, _) => R(Checks.L4, p, k, Classification.Pass, stored),
            (Provider.PostgreSql, _, true) => R(Checks.L4, p, k, Classification.ExpectedDifference, stored + " (Npgsql ±infinity mapping)"),
            _ => R(Checks.L4, p, k, Classification.FailDefect, "unexpected storage: " + stored),
        };
    }

    /// <summary>
    /// The approved runtime contract is the APPLICATION guard (UtcDateTime.ToStore): Local and
    /// Unspecified are refused before reaching the provider. A provider-level error does not count.
    /// </summary>
    public static CheckResult Utc(Provider p, DbContextKind k, UtcMeasurement m)
    {
        var problems = new List<string>();
        if (!m.UtcRoundTripExact || m.UtcReadKind != DateTimeKind.Utc) problems.Add($"UTC round-trip exact={m.UtcRoundTripExact} kind={m.UtcReadKind}");
        if (!m.LocalRejectedByApplicationGuard) problems.Add("Local not rejected by the application UTC guard");
        if (m.LocalRowPersisted) problems.Add("Local row persisted");
        if (!m.UnspecifiedRejectedByApplicationGuard) problems.Add("Unspecified not rejected by the application UTC guard");
        if (m.UnspecifiedRowPersisted) problems.Add("Unspecified row persisted");
        if (!m.NullableNullRoundTrip) problems.Add("nullable DateTime null did not round-trip as SQL NULL");

        return problems.Count == 0
            ? R(Checks.L5, p, k, Classification.Pass, "UTC exact + Kind=Utc; Local/Unspecified refused by application guard, nothing persisted; NULL preserved")
            : R(Checks.L5, p, k, Classification.FailDefect, string.Join("; ", problems) + (m.Notes.Length > 0 ? $" ({m.Notes})" : ""));
    }

    /// <summary>Intended semantics on both engines: many NULL TenantIds allowed; same tenant + name refused.</summary>
    public static CheckResult NullableUnique(Provider p, DbContextKind k, NullableUniqueMeasurement m) =>
        m.NullTenantDuplicatesAccepted && m.SameTenantDuplicateRejected
            ? R(Checks.L6, p, k, Classification.Pass, "duplicate names with NULL TenantId accepted; duplicate within one tenant rejected")
            : R(Checks.L6, p, k, Classification.FailDefect,
                $"null-duplicates-accepted={m.NullTenantDuplicatesAccepted}, same-tenant-duplicate-rejected={m.SameTenantDuplicateRejected} {m.Notes}".Trim());

    public static CheckResult ForeignKeys(Provider p, DbContextKind k, ForeignKeyMeasurement m)
    {
        var ok = m.OrphanInsertRejected && m.CascadeRemovedDependents && m.RestrictBlockedPrincipalDelete && m.RestrictPrincipalSurvived;
        return ok
            ? R(Checks.L7, p, k, Classification.Pass, "orphan insert rejected; Cascade removed dependents; Restrict blocked delete")
            : R(Checks.L7, p, k, Classification.FailDefect,
                $"orphan-rejected={m.OrphanInsertRejected}, cascade={m.CascadeRemovedDependents}, restrict-blocked={m.RestrictBlockedPrincipalDelete}, " +
                $"principal-survived={m.RestrictPrincipalSurvived} {m.Notes}".Trim());
    }

    /// <summary>
    /// L-8, COLLATION-AWARE (D-k11). Case sensitivity is an ENVIRONMENT property — SQL Server
    /// collations may be CI or CS; PostgreSQL deterministic collations are CS, nondeterministic
    /// ICU ones may not be — so it is MEASURED on the real column, never assumed per provider
    /// and never inferred from a collation name.
    ///
    ///   1. Tenant.Code application lookup must resolve a case variant on ANY collation — the
    ///      P1-f canonicalization contract. A miss is FAIL — DEFECT, whatever the collation.
    ///   2. The two column observations must agree (a CI column matches a case variant AND
    ///      rejects it as a duplicate; a CS column does neither). Disagreement is FAIL — DEFECT.
    ///   3. The provider's baseline (SQL Server default CI; PostgreSQL default CS) is PASS for
    ///      SQL Server. Any case-sensitive column — PostgreSQL by default, or a CS SQL Server
    ///      deployment — is EXPECTED DIFFERENCE — RECORDED, environment-determined; for
    ///      Plant.Code it is the known DB-09 divergence. A CI PostgreSQL column is likewise a
    ///      recorded, environment-determined difference.
    /// </summary>
    public static CheckResult CodeCase(Provider p, DbContextKind k, CodeCaseMeasurement m)
    {
        var evidence = (m.Collation is null ? string.Empty : $"; collation {m.Collation}")
                       + (m.LiteralComparisonCaseInsensitive is null ? string.Empty
                           : $"; database-default literal comparison case-insensitive={m.LiteralComparisonCaseInsensitive}");

        if (m.ApplicationLookupResolvesAnyCase == false)
            return R(Checks.L8, p, k, Classification.FailDefect,
                $"{m.Entity}: application lookup did not resolve a case variant — P1-f canonicalization contract broken, regardless of collation{evidence}");

        var app = m.ApplicationLookupResolvesAnyCase == true ? "application lookup resolves any case; " : string.Empty;
        var observed = $"{m.Entity}: {app}column comparison case-variant match={m.RawCaseVariantMatches}, " +
                       $"case-variant duplicate accepted={m.CaseVariantDuplicateAccepted}{evidence}";

        var caseInsensitive = m.RawCaseVariantMatches && !m.CaseVariantDuplicateAccepted;
        var caseSensitive = !m.RawCaseVariantMatches && m.CaseVariantDuplicateAccepted;
        if (!caseInsensitive && !caseSensitive)
            return R(Checks.L8, p, k, Classification.FailDefect,
                observed + " — inconsistent: lookup and uniqueness disagree on case sensitivity");

        var plant = m.Entity == "Plant.Code";
        if (caseInsensitive)
            return p == Provider.SqlServer
                ? R(Checks.L8, p, k, Classification.Pass, observed + " — measured case-insensitive (SQL Server baseline)")
                : R(Checks.L8, p, k, Classification.ExpectedDifference,
                    observed + " — environment-determined: this PostgreSQL column compares case-insensitively");

        var note = plant
            ? " — KNOWN Plant.Code provider divergence (no application canonicalization; DB-09)"
            : " — P1-f canonicalization keeps application behaviour identical (documented residual risk for writes outside the application)";
        var environment = p == Provider.SqlServer
            ? " — environment-determined: this SQL Server collation is case-sensitive"
            : " — measured case-sensitive (PostgreSQL baseline)";
        return R(Checks.L8, p, k, Classification.ExpectedDifference, observed + environment + note);
    }

    /// <summary>
    /// A seeder that LOGGED an error fails L-9 first — before counts, idempotency or
    /// fingerprints — because IdentitySeeder swallows its exceptions and a partial or even
    /// complete-looking seed must not pass when it reported a failure.
    /// </summary>
    public static CheckResult Seed(Provider p, DbContextKind k, SeedMeasurement m)
    {
        string[] core = ["Tenants", "Users", "Roles", "Permissions", "Modules", "Menus"];
        var empty = core.Where(t => m.Counts.GetValueOrDefault(t) == 0).ToArray();
        var counts = string.Join(", ", m.Counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

        if (m.SeederErrors is { Count: > 0 } errors)
        {
            var shown = errors.Take(3).Select(e => Redact.Secrets(e.Length > 200 ? e[..200] + "…" : e));
            var more = errors.Count > 3 ? $" (+{errors.Count - 3} more)" : string.Empty;
            return R(Checks.L9, p, k, Classification.FailDefect,
                $"IdentitySeeder logged {errors.Count} error(s): [{string.Join(" | ", shown)}]{more}; {counts}");
        }

        if (empty.Length > 0)
            return R(Checks.L9, p, k, Classification.FailDefect, $"seed produced no rows in [{string.Join(",", empty)}]; {counts}");
        if (!m.SecondRunChangedNothing)
            return R(Checks.L9, p, k, Classification.FailDefect, $"second seeder run changed row counts (not idempotent); {counts}");
        return R(Checks.L9, p, k, Classification.Pass, $"{counts}; structure fingerprint {m.Fingerprint[..16]}; idempotent");
    }

    /// <summary>Cross-provider: the non-secret seeded structure must be identical.</summary>
    public static CheckResult SeedEquivalence(SeedMeasurement? sqlServer, SeedMeasurement? postgreSql)
    {
        const string scope = "Cross/Master";
        var title = Checks.Title(Checks.L9 + "x");
        if (sqlServer is null || postgreSql is null)
            return new CheckResult(Checks.L9, title, scope, Classification.SkippedNoEndpoint,
                "needs seeded structure from BOTH providers; " +
                $"SqlServer={(sqlServer is null ? "unavailable" : "available")}, PostgreSql={(postgreSql is null ? "unavailable" : "available")}");

        if (sqlServer.Fingerprint == postgreSql.Fingerprint)
            return new CheckResult(Checks.L9, title, scope, Classification.Pass, $"identical non-secret structure (fingerprint {sqlServer.Fingerprint[..16]})");

        var differing = sqlServer.Counts.Keys.Union(postgreSql.Counts.Keys)
            .Where(t => sqlServer.Counts.GetValueOrDefault(t) != postgreSql.Counts.GetValueOrDefault(t))
            .Select(t => $"{t}: {sqlServer.Counts.GetValueOrDefault(t)} vs {postgreSql.Counts.GetValueOrDefault(t)}");
        return new CheckResult(Checks.L9, title, scope, Classification.FailDefect,
            $"seeded structure differs; count differences [{string.Join("; ", differing)}] (fingerprints differ)");
    }

    public static CheckResult DownUp(Provider p, DbContextKind k, DownUpMeasurement m, ContextExpectations e)
    {
        var ok = m.TablesAfterDown == 0 && m.HistoryRowsAfterDown == 0
                 && m.TablesAfterUp == e.Tables.Length && m.HistoryAfterUp.SequenceEqual(e.Lineage(p));
        return ok
            ? R(Checks.L11, p, k, Classification.Pass, $"Down -> 0 tables, empty history; Up -> {m.TablesAfterUp} tables, lineage restored")
            : R(Checks.L11, p, k, Classification.FailDefect,
                $"after Down: tables={m.TablesAfterDown}, history={m.HistoryRowsAfterDown}; after Up: tables={m.TablesAfterUp}, history=[{string.Join(",", m.HistoryAfterUp)}]");
    }

    /// <summary>AppDb L-10: no incremental AppDb PostgreSQL migration exists, so there is nothing to upgrade.</summary>
    public static CheckResult DeferredAppDb() => new(Checks.L10, Checks.Title(Checks.L10), "All/AppDb", Classification.Deferred,
        "no incremental AppDb PostgreSQL migration exists yet; the AppDb upgrade path cannot be exercised without fabricating one");

    /// <summary>
    /// L-10 (Master) — incremental upgrade from the Phase 1 head through the pinned upgrade
    /// migration, with pre-existing rows. PASS only when EVERY assertion below holds; the first
    /// problems found are named, with the stage and table they concern.
    ///
    /// Histories are checked as exact prefixes of the approved lineage ending at the pinned
    /// From / To migrations, so this check judges the upgrade transition itself; full-lineage
    /// equality remains L-2's job.
    /// </summary>
    public static CheckResult Upgrade(Provider p, DbContextKind k, UpgradeMeasurement m, ContextExpectations e)
    {
        if (e.Upgrade is not { } u)
            return R(Checks.L10, p, k, Classification.FailDefect, "no upgrade expectation pinned for this context");

        var lineage = e.Lineage(p);
        var from = u.From(p);
        var to = u.To(p);
        var fromIndex = Array.IndexOf(lineage, from);
        var toIndex = Array.IndexOf(lineage, to);
        if (fromIndex < 0 || toIndex != fromIndex + 1)
            return R(Checks.L10, p, k, Classification.FailDefect,
                $"expectations inconsistent: '{from}' must be immediately followed by '{to}' in the approved lineage [{string.Join(", ", lineage)}]");

        var beforeHistory = lineage[..(fromIndex + 1)];
        var upHistory = lineage[..(toIndex + 1)];
        var problems = new List<string>();

        void History(UpgradeStage s, string[] expected)
        {
            if (!s.History.SequenceEqual(expected))
                problems.Add($"{s.Name}: history [{string.Join(", ", s.History)}] != [{string.Join(", ", expected)}]");
        }

        void Absent(UpgradeStage s)
        {
            if (s.Shape.CompaniesTable) problems.Add($"{s.Name}: Companies table present");
            if (s.Shape.TenantCompanyIdColumn) problems.Add($"{s.Name}: Tenants.CompanyId present");
        }

        void Upgraded(UpgradeStage s)
        {
            var schema = Schema(p, k, s.Schema, e);
            if (schema.Classification != Classification.Pass) problems.Add($"{s.Name}: schema {schema.Detail}");

            var sh = s.Shape;
            if (!sh.CompaniesTable) problems.Add($"{s.Name}: Companies table missing");
            if (!sh.CompanyColumns.Order(StringComparer.Ordinal).SequenceEqual(u.CompanyColumns.Order(StringComparer.Ordinal)))
                problems.Add($"{s.Name}: Companies columns [{string.Join(",", sh.CompanyColumns)}] != [{string.Join(",", u.CompanyColumns)}]");
            if (sh.NullableCompanyColumns.Length > 0) problems.Add($"{s.Name}: nullable Companies columns [{string.Join(",", sh.NullableCompanyColumns)}]");
            if (!sh.TenantCompanyIdColumn) problems.Add($"{s.Name}: Tenants.CompanyId missing");
            else if (!sh.TenantCompanyIdNullable) problems.Add($"{s.Name}: Tenants.CompanyId is NOT NULL");
            if (sh.CodeIndexUnique != true) problems.Add($"{s.Name}: {u.CodeIndex} unique={sh.CodeIndexUnique?.ToString() ?? "missing"}");
            if (!sh.TenantCompanyIdIndex) problems.Add($"{s.Name}: {u.TenantCompanyIdIndex} missing");
            if (sh.ForeignKeyDeleteAction != u.DeleteAction(p))
                problems.Add($"{s.Name}: {u.ForeignKey} delete action '{sh.ForeignKeyDeleteAction ?? "missing"}' != '{u.DeleteAction(p)}'");
            if (s.CompaniesRows != 0) problems.Add($"{s.Name}: Companies has {s.CompaniesRows} row(s), expected 0");
            if (s.TenantsWithCompany != 0) problems.Add($"{s.Name}: {s.TenantsWithCompany} tenant(s) with non-NULL CompanyId, expected 0");
        }

        void SameData(UpgradeStage s)
        {
            var tables = m.Before.Fingerprints.Keys.Union(s.Fingerprints.Keys).Order(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                var before = m.Before.Fingerprints.GetValueOrDefault(table);
                var now = s.Fingerprints.GetValueOrDefault(table);
                if (before is null || now is null || before != now)
                    problems.Add($"{s.Name}: {table} fingerprint {Short(now)} != before {Short(before)}");
            }
        }

        if (m.FixtureRows != u.FixtureRows) problems.Add($"fixture rows {m.FixtureRows} != {u.FixtureRows}");
        foreach (var (table, expectedRows) in UpgradeFixtureCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!m.BeforeRowCounts.TryGetValue(table, out var actualRows))
                problems.Add($"before: {table} persisted row count not measured, expected {expectedRows}");
            else if (actualRows != expectedRows)
                problems.Add($"before: {table} has {actualRows} persisted row(s), expected {expectedRows}");
        }
        if (m.Before.Fingerprints.Count != u.FixtureTables.Length
            || !m.Before.Fingerprints.Keys.Order(StringComparer.Ordinal).SequenceEqual(u.FixtureTables.Order(StringComparer.Ordinal)))
            problems.Add($"before: fingerprinted tables [{string.Join(",", m.Before.Fingerprints.Keys)}] != [{string.Join(",", u.FixtureTables)}]");

        History(m.Before, beforeHistory);
        Absent(m.Before);

        History(m.AfterUp, upHistory);
        Upgraded(m.AfterUp);
        SameData(m.AfterUp);

        foreach (var name in u.Probes)
        {
            var probe = m.Probes.FirstOrDefault(x => x.Name == name);
            if (probe is null) problems.Add($"probe {name}: not run");
            else if (!probe.Rejected) problems.Add($"probe {name}: not rejected ({probe.Detail})");
            else if (!probe.RolledBack) problems.Add($"probe {name}: rollback not confirmed ({probe.Detail})");
        }

        History(m.AfterDown, beforeHistory);
        Absent(m.AfterDown);
        if (!SameSchema(m.AfterDown.Schema, m.Before.Schema))
            problems.Add($"{m.AfterDown.Name}: schema differs from before-upgrade schema");
        SameData(m.AfterDown);

        History(m.AfterReUp, upHistory);
        Upgraded(m.AfterReUp);
        SameData(m.AfterReUp);

        return problems.Count == 0
            ? R(Checks.L10, p, k, Classification.Pass,
                $"{from} -> {to} -> {from} -> {to} with {m.FixtureRows} pre-existing rows: data unchanged at every stage; " +
                $"Companies created empty, Tenants.CompanyId NULL, {u.ForeignKey} {u.DeleteAction(p)}; {m.Probes.Count} probes rejected and rolled back")
            : R(Checks.L10, p, k, Classification.FailDefect,
                string.Join("; ", problems.Take(8)) + (problems.Count > 8 ? $" (+{problems.Count - 8} more)" : string.Empty));
    }

    /// <summary>
    /// F2 — the exact rows the L-10 fixtures must have PERSISTED at the Phase 1 head, per table
    /// (sums to <see cref="UpgradeExpectation.FixtureRows"/>). Checked against database counts.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, long> UpgradeFixtureCounts = new Dictionary<string, long>
    {
        ["Tenants"] = 2, ["Users"] = 2, ["UserTenants"] = 3, ["Roles"] = 2, ["UserRoles"] = 2,
    };

    private static bool SameSchema(SchemaMeasurement a, SchemaMeasurement b) =>
        a.Tables.Order(StringComparer.Ordinal).SequenceEqual(b.Tables.Order(StringComparer.Ordinal))
        && a.Columns == b.Columns
        && a.ColumnCategories.OrderBy(kv => kv.Key, StringComparer.Ordinal).SequenceEqual(b.ColumnCategories.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        && a.Indexes == b.Indexes && a.UniqueIndexes == b.UniqueIndexes
        && a.FilteredIndexes == b.FilteredIndexes && a.ForeignKeys == b.ForeignKeys;

    private static string Short(string? fingerprint) => fingerprint is null ? "missing" : fingerprint[..Math.Min(12, fingerprint.Length)];

    public static CheckResult Skipped(string id, Provider p, DbContextKind k, string reason) =>
        R(id, p, k, Classification.SkippedNoEndpoint, reason);

    public static CheckResult Failed(string id, Provider p, DbContextKind k, string reason) =>
        R(id, p, k, Classification.FailDefect, reason);

    /// <summary>D-k9: the environment prevented verification. Still a failure (exit 1), never a skip.</summary>
    public static CheckResult Infrastructure(string id, Provider p, DbContextKind k, string reason) =>
        R(id, p, k, Classification.FailInfrastructure, reason);
}
