using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using ERP.Identity.Entities;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ERP.ParityCheck;

/// <summary>
/// OFFLINE SIMULATION. Canned measurements — modelled on the approved provider behaviour —
/// are fed through the SAME <see cref="Evaluator"/> and <see cref="Report"/> as a live run.
/// Nothing here opens a connection, and every simulated report is labelled NOT live evidence.
/// Simulation exists to exercise classification, coverage reporting and exit codes; it can
/// never make a live run pass.
/// </summary>
public static class Simulation
{
    public static Report Run(Expectations expectations, IReadOnlySet<Provider> available)
    {
        var report = new Report(RunMode.Simulation);
        var seeds = new Dictionary<Provider, SeedMeasurement?>();

        foreach (var p in new[] { Provider.SqlServer, Provider.PostgreSql })
        {
            foreach (var kind in new[] { DbContextKind.Master, DbContextKind.AppDb })
            {
                if (!available.Contains(p))
                {
                    foreach (var (id, k) in Checks.For(kind))
                        report.Add(Evaluator.Skipped(id, p, k, "simulated: no endpoint for this provider"));
                    continue;
                }

                var e = expectations.For(kind);
                var truth = Expectations.Approved.For(kind);
                foreach (var (id, k) in Checks.For(kind))
                {
                    report.Add(id switch
                    {
                        Checks.L1 => Evaluator.Schema(p, k, Schema(p, truth), e),
                        Checks.L2 => Evaluator.History(p, k, truth.Lineage(p), e),
                        Checks.L3 => Evaluator.Precision(p, k, Precision(p)),
                        Checks.L4 => Evaluator.DateOnlyExtremes(p, k, DateOnlyExtremes(p)),
                        Checks.L5 => Evaluator.Utc(p, k, new UtcMeasurement(true, DateTimeKind.Utc, true, false, true, false, true, "")),
                        Checks.L6 => Evaluator.NullableUnique(p, k, new NullableUniqueMeasurement(true, true, "")),
                        Checks.L7 => Evaluator.ForeignKeys(p, k, new ForeignKeyMeasurement(true, true, true, true, "")),
                        Checks.L8 => Evaluator.CodeCase(p, k, CodeCase(p, k)),
                        Checks.L9 => Evaluator.Seed(p, k, seeds[p] = Seed()),
                        Checks.L11 => Evaluator.DownUp(p, k, new DownUpMeasurement(0, 0, truth.Tables.Length, truth.Lineage(p)), e),
                        Checks.L10 => Evaluator.Upgrade(p, k, Upgrade(p, truth), e),
                        _ => throw new InvalidOperationException(id),
                    });
                }
            }
        }

        report.Add(Evaluator.SeedEquivalence(seeds.GetValueOrDefault(Provider.SqlServer), seeds.GetValueOrDefault(Provider.PostgreSql)));
        report.Add(Evaluator.DeferredAppDb());
        return report;
    }

    /// <summary>
    /// A canned, fully consistent Master L-10 cycle: Phase 1 head -> upgrade -> back -> upgrade,
    /// identical fingerprints throughout, Company objects present only after an Up, every probe
    /// rejected and rolled back. The self-test derives each failure scenario from this.
    /// </summary>
    internal static UpgradeMeasurement Upgrade(Provider p, ContextExpectations truth)
    {
        var u = truth.Upgrade ?? throw new InvalidOperationException("no upgrade expectation pinned");
        var lineage = truth.Lineage(p);
        var from = Array.IndexOf(lineage, u.From(p));
        var before = lineage[..(from + 1)];
        var up = lineage[..(from + 2)];

        var upgraded = Schema(p, truth);
        var phase1 = upgraded with { Tables = upgraded.Tables.Where(t => t != "Companies").ToArray() };
        var shape = new CompanyShape(true, u.CompanyColumns, [], true, true, true, true, u.DeleteAction(p));
        var fingerprints = u.FixtureTables.ToDictionary(
            t => t, t => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("simulated-fixture-" + t))).ToLowerInvariant());

        return new UpgradeMeasurement(
            new UpgradeStage("before", before, phase1, CompanyShape.Absent, fingerprints, -1, -1),
            new UpgradeStage("after-up", up, upgraded, shape, fingerprints, 0, 0),
            new UpgradeStage("after-down", before, phase1, CompanyShape.Absent, fingerprints, -1, -1),
            new UpgradeStage("after-re-up", up, upgraded, shape, fingerprints, 0, 0),
            u.Probes.Select(n => new ProbeResult(n, Rejected: true, RolledBack: true, "simulated")).ToList(),
            u.FixtureRows,
            new Dictionary<string, long>(Evaluator.UpgradeFixtureCounts));
    }

    private static SchemaMeasurement Schema(Provider p, ContextExpectations truth) => new(
        truth.Tables, truth.Columns, truth.ColumnCategories, truth.Indexes, truth.UniqueIndexes, truth.FilteredIndexes(p), truth.ForeignKeys);

    // SQL Server keeps 100 ns; PostgreSQL truncates to 1 µs (as observed in P1-i/P1-j).
    private static PrecisionMeasurement Precision(Provider p)
    {
        const long aligned = 639_270_477_306_543_210, sub = 639_270_181_231_234_567;
        return new PrecisionMeasurement(aligned, aligned, sub, p == Provider.SqlServer ? sub : sub - sub % 10, DateTimeKind.Utc);
    }

    private static DateOnlyMeasurement DateOnlyExtremes(Provider p) => p == Provider.SqlServer
        ? new("0001-01-01", DateOnly.MinValue, "9999-12-31", DateOnly.MaxValue)
        : new("-infinity", DateOnly.MinValue, "infinity", DateOnly.MaxValue);

    private static CodeCaseMeasurement CodeCase(Provider p, DbContextKind k)
    {
        var caseInsensitive = p == Provider.SqlServer;
        return k == DbContextKind.Master
            ? new CodeCaseMeasurement("Tenant.Code", true, caseInsensitive, !caseInsensitive)
            : new CodeCaseMeasurement("Plant.Code", null, caseInsensitive, !caseInsensitive);
    }

    private static SeedMeasurement Seed() => new(
        new Dictionary<string, int> { ["Tenants"] = 1, ["Users"] = 4, ["Roles"] = 5, ["Permissions"] = 20, ["Modules"] = 3, ["Menus"] = 10 },
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("simulated-seed-structure"))),
        SecondRunChangedNothing: true);
}

/// <summary>
/// SELF-TEST: runs the approved positive controls PC-L1..PC-L4 plus the exit-code contract,
/// asserting each outcome. Never contacts a database (PC-L1 drives the real live runner with
/// an EMPTY environment, or with live mode off).
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;
        void Expect(string control, bool condition, string what)
        {
            output.WriteLine($"  [{(condition ? "ok" : "FAILED")}] {control}: {what}");
            if (!condition) failures++;
        }

        var all = new HashSet<Provider> { Provider.SqlServer, Provider.PostgreSql };
        var contexts = new HashSet<DbContextKind> { DbContextKind.Master, DbContextKind.AppDb };
        var matrix = 2 * (Checks.For(DbContextKind.Master).Count() + Checks.For(DbContextKind.AppDb).Count()) + 2;

        // PC-L1a — live mode requested, no endpoint configured.
        output.WriteLine("PC-L1a: --live with NO endpoint configured (real live runner, empty environment)");
        var l1a = new Report(RunMode.Live);
        await LiveRunner.RunAsync(l1a, new RunOptions(true, all, contexts), new Dictionary<string, string?>(), Expectations.Approved, CancellationToken.None);
        Expect("PC-L1a", l1a.Results.Count == matrix, $"full matrix reported ({l1a.Results.Count}/{matrix})");
        Expect("PC-L1a", l1a.Count(Classification.Pass) == 0 && l1a.Count(Classification.ExpectedDifference) == 0, "no live check reports PASS or EXPECTED DIFFERENCE");
        Expect("PC-L1a", l1a.Count(Classification.SkippedNoEndpoint) == matrix - 1 && l1a.Count(Classification.Deferred) == 1
                         && l1a.Results.Single(r => r.Classification == Classification.Deferred).Scope == "All/AppDb",
            "every check SKIPPED (Master L-10 included) except AppDb L-10 DEFERRED");
        Expect("PC-L1a", l1a.ExitCode == ExitCodes.Incomplete, $"exit code {l1a.ExitCode} == {ExitCodes.Incomplete} (incomplete)");
        Expect("PC-L1a", l1a.Verdict.Contains("NOT verified") && !l1a.Verdict.Contains("Full live matrix executed"), "no live-verification success claim");

        // PC-L1b — endpoints present in the environment, but live mode NOT authorized.
        output.WriteLine("PC-L1b: endpoints configured but --live NOT supplied (must not connect)");
        var l1b = new Report(RunMode.Live);
        var unreachable = new Dictionary<string, string?>
        {
            [Endpoint.SqlServerVariable] = "Server=pc-l1b.invalid;Connect Timeout=1;Trusted_Connection=true;",
            [Endpoint.PostgreSqlVariable] = "Host=pc-l1b.invalid;Timeout=1;Username=nobody",
        };
        await LiveRunner.RunAsync(l1b, new RunOptions(false, all, contexts), unreachable, Expectations.Approved, CancellationToken.None);
        Expect("PC-L1b", l1b.Count(Classification.Pass) == 0 && l1b.Count(Classification.FailDefect) == 0, "no PASS, and no connection attempt (an attempt would FAIL as unreachable)");
        Expect("PC-L1b", l1b.Results.Where(r => r.Scope != "Cross/Master").All(r =>
                r.Classification == Classification.Deferred
                || (r.Classification == Classification.SkippedNoEndpoint && r.Detail.Contains("--live not supplied"))),
            "every per-provider check SKIPPED, naming the missing authorization");
        Expect("PC-L1b", l1b.Results.Single(r => r.Scope == "Cross/Master").Classification == Classification.SkippedNoEndpoint,
            "cross-provider seed equivalence SKIPPED (no seeded structure)");
        Expect("PC-L1b", l1b.ExitCode == ExitCodes.Incomplete, $"exit code {l1b.ExitCode} == {ExitCodes.Incomplete}");

        // PC-L2 — deliberately wrong expected value (offline): an extra migration ID.
        output.WriteLine("PC-L2: deliberately wrong expected PostgreSQL Master lineage (simulation)");
        var wrong = Expectations.Approved with
        {
            Master = Expectations.Approved.Master with { PostgreSqlLineage = [.. Expectations.Approved.Master.PostgreSqlLineage, "29990101000000_NotARealMigration"] },
        };
        var l2 = Simulation.Run(wrong, all);
        var failed = l2.Results.Where(r => r.Classification == Classification.FailDefect).ToList();
        Expect("PC-L2", failed.Count == 2 && failed.All(r => r.Scope == "PostgreSql/Master" && r.Id is Checks.L2 or Checks.L11),
            $"exactly the lineage-dependent checks fail ({string.Join(", ", failed.Select(r => $"{r.Id} {r.Scope}"))})");
        Expect("PC-L2", l2.ExitCode == ExitCodes.Failure, $"exit code {l2.ExitCode} == {ExitCodes.Failure}");

        // PC-L3 — one provider available, the other not.
        output.WriteLine("PC-L3: SQL Server available, PostgreSQL unavailable (simulation)");
        var l3 = Simulation.Run(Expectations.Approved, new HashSet<Provider> { Provider.SqlServer });
        Expect("PC-L3", l3.Results.Where(r => r.Scope.StartsWith("SqlServer/")).All(r => r.Classification == Classification.Pass), "SQL Server checks classified");
        Expect("PC-L3", l3.Results.Where(r => r.Scope.StartsWith("PostgreSql/")).All(r => r.Classification == Classification.SkippedNoEndpoint), "PostgreSQL checks SKIPPED");
        Expect("PC-L3", l3.Results.Single(r => r.Scope == "Cross/Master").Classification == Classification.SkippedNoEndpoint, "cross-provider seed equivalence SKIPPED");
        Expect("PC-L3", l3.ExitCode == ExitCodes.Incomplete && l3.Verdict.Contains("PARTIAL COVERAGE — only SqlServer"), "partial coverage stated; exit code 2");

        // PC-L4 — normal simulated run.
        output.WriteLine("PC-L4: normal run, both providers (simulation)");
        var l4 = Simulation.Run(Expectations.Approved, all);
        var differences = l4.Results.Where(r => r.Classification == Classification.ExpectedDifference).Select(r => $"{r.Id} {r.Scope}").Order().ToArray();
        Expect("PC-L4", l4.Count(Classification.FailDefect) == 0 && l4.Count(Classification.SkippedNoEndpoint) == 0, "no FAIL, no SKIPPED");
        Expect("PC-L4", differences.SequenceEqual(["L-3 PostgreSql/Master", "L-4 PostgreSql/AppDb", "L-8 PostgreSql/AppDb", "L-8 PostgreSql/Master"]),
            $"expected differences exactly as approved ({string.Join(", ", differences)})");
        Expect("PC-L4", l4.Count(Classification.Deferred) == 1
                        && l4.Results.Single(r => r.Classification == Classification.Deferred) is { Id: Checks.L10, Scope: "All/AppDb" },
            "AppDb L-10 explicitly DEFERRED (the only deferral)");
        Expect("PC-L4", l4.Count(Classification.Pass) == matrix - differences.Length - 1, $"PASS count {l4.Count(Classification.Pass)} == {matrix - differences.Length - 1}");
        Expect("PC-L4", l4.Verdict.Contains("SIMULATION — NOT LIVE EVIDENCE") && l4.Verdict.Contains("No live parity claim") && l4.Verdict.Contains("DB-03 PARTIALLY OPEN"),
            "no misleading success claim; DB-03 PARTIALLY OPEN");
        Expect("PC-L4", l4.ExitCode == ExitCodes.Success, $"exit code {l4.ExitCode} == {ExitCodes.Success}");

        // Exit-code precedence contract: 5 > 4 > 1 > 2 > 0.
        output.WriteLine("EXIT-CODES: precedence contract");
        var precedence = Simulation.Run(Expectations.Approved, new HashSet<Provider> { Provider.SqlServer });
        precedence.Add(Evaluator.Failed(Checks.L1, Provider.SqlServer, DbContextKind.Master, "synthetic"));
        Expect("EXIT-CODES", precedence.ExitCode == ExitCodes.Failure, "FAIL outranks SKIPPED (1)");
        precedence.SafetyRefusals.Add("synthetic refusal");
        Expect("EXIT-CODES", precedence.ExitCode == ExitCodes.SafetyRefusal, "safety refusal outranks FAIL (4)");
        precedence.CleanupFailures.Add("synthetic cleanup failure");
        Expect("EXIT-CODES", precedence.ExitCode == ExitCodes.CleanupFailure, "cleanup failure outranks everything (5)");

        // Safety guards (pure, offline).
        output.WriteLine("SAFETY: name guards");
        Expect("SAFETY", Throws(() => Safety.AssertApprovedTarget(Provider.PostgreSql, "ERP_PG_Dev")), "ERP_PG_Dev refused");
        Expect("SAFETY", Throws(() => Safety.AssertApprovedTarget(Provider.SqlServer, "ERP_Master")), "ERP_Master refused");
        Expect("SAFETY", Throws(() => Safety.AssertApprovedTarget(Provider.SqlServer, Safety.PostgreSqlDatabase)), "cross-provider name refused");
        Expect("SAFETY", !Throws(() => Safety.AssertApprovedTarget(Provider.SqlServer, Safety.SqlServerDatabase)), "approved SQL Server name accepted");
        Expect("SAFETY", Throws(() => Endpoint.Resolve(Provider.PostgreSql, new Dictionary<string, string?>
            { [Endpoint.PostgreSqlVariable] = "Host=localhost;Database=ERP_PG_Dev;Username=nobody" }).Validate()), "endpoint naming ERP_PG_Dev refused");
        Expect("SAFETY", Redact.Secrets("Host=x;Username=u;Password=s3cret;") == "Host=x;Username=<redacted>;Password=<redacted>;", "credentials redacted from output");

        // ── D-k9 / D-k11 / Low-1 correction controls (C1..C10) — synthetic data only ──
        const string db03 = "DB-03 PARTIALLY OPEN";

        output.WriteLine("C1: missing endpoint -> SKIPPED (real live runner, empty environment)");
        var c1 = new Report(RunMode.Live);
        await LiveRunner.RunAsync(c1, new RunOptions(true, all, contexts), new Dictionary<string, string?>(), Expectations.Approved, CancellationToken.None);
        Expect("C1", c1.Results.Where(r => r.Scope != "All/AppDb").All(r => r.Classification == Classification.SkippedNoEndpoint),
            "every check SKIPPED, Master L-10 included");
        Expect("C1", c1.Count(Classification.FailInfrastructure) == 0 && c1.Count(Classification.Pass) == 0, "no infrastructure failure, no PASS");
        Expect("C1", c1.ExitCode == ExitCodes.Incomplete && c1.Verdict.Contains("NOT verified"), $"exit {c1.ExitCode} == 2; no live success claim");

        output.WriteLine("C2: infrastructure failures -> FAIL — INFRASTRUCTURE (D-k9)");
        Exception[] infrastructure =
        [
            new System.Net.Sockets.SocketException(11001), new TimeoutException("synthetic"),
            new PostgresException("synthetic", "FATAL", "FATAL", "08006"), new PostgresException("synthetic", "FATAL", "FATAL", "28P01"),
            new PostgresException("synthetic", "FATAL", "FATAL", "3D000"), new NpgsqlException("synthetic connection failure"),
            new InvalidOperationException("wrapper", new System.Net.Sockets.SocketException(10061)),
        ];
        Exception[] notInfrastructure =
        [
            new PostgresException("dup", "ERROR", "ERROR", "23505"), new PostgresException("fk", "ERROR", "ERROR", "23503"),
            new InvalidOperationException("Refusing to persist a DateTime with Kind=Local"), new ArgumentException("assertion-like"),
        ];
        Expect("C2", infrastructure.All(LiveRunner.IsInfrastructureFailure), "connection/auth/catalog/socket/timeout failures classified infrastructure");
        Expect("C2", !notInfrastructure.Any(LiveRunner.IsInfrastructureFailure), "constraint violations and the UTC guard are NOT infrastructure");
        var c2 = new Report(RunMode.Live);
        c2.Add(Evaluator.Infrastructure(Checks.L1, Provider.PostgreSql, DbContextKind.Master, "configured endpoint unreachable: SocketException: synthetic"));
        c2.Add(Evaluator.Skipped(Checks.L1, Provider.SqlServer, DbContextKind.Master, "not set"));
        Expect("C2", c2.Count(Classification.FailInfrastructure) == 1 && c2.Count(Classification.FailDefect) == 0, "counted as INFRASTRUCTURE, separately from DEFECT");
        Expect("C2", c2.ExitCode == ExitCodes.Failure, $"exit {c2.ExitCode} == 1 (failure outranks skip)");
        Expect("C2", c2.Render().Contains("[FAIL — INFRASTRUCTURE]") && c2.Render().Contains("FAIL-INFRASTRUCTURE=1") && c2.ToJson().Contains("FAIL \\u2014 INFRASTRUCTURE"),
            "label and separate count in console and JSON");
        Expect("C2", c2.Verdict.Contains("0 parity defect(s), 1 infrastructure failure(s)") && c2.Verdict.Contains("NOT verified") && c2.Verdict.Contains(db03),
            "verdict separates causes and claims nothing");

        output.WriteLine("C3: wrong parity expectation -> FAIL — DEFECT");
        var c3 = Simulation.Run(wrong, all);
        Expect("C3", c3.Count(Classification.FailDefect) == 2 && c3.Count(Classification.FailInfrastructure) == 0, "2 DEFECT, 0 INFRASTRUCTURE");
        Expect("C3", c3.ExitCode == ExitCodes.Failure && c3.Verdict.Contains("2 parity defect(s), 0 infrastructure failure(s)"), $"exit {c3.ExitCode} == 1; verdict separates causes");

        output.WriteLine("C4: case-insensitive SQL Server (measured) -> PASS (D-k11)");
        var ci = Evaluator.CodeCase(Provider.SqlServer, DbContextKind.Master, new("Tenant.Code", true, true, false,
            "server=SQL_Latin1_General_CP1_CI_AS, database=SQL_Latin1_General_CP1_CI_AS, Tenants.Code=database default", true));
        var ciPlant = Evaluator.CodeCase(Provider.SqlServer, DbContextKind.AppDb, new("Plant.Code", null, true, false, "database=SQL_Latin1_General_CP1_CI_AS", true));
        Expect("C4", ci.Classification == Classification.Pass && ci.Detail.Contains("collation server=SQL_Latin1_General_CP1_CI_AS"), "Tenant.Code PASS; collation evidence recorded");
        Expect("C4", ciPlant.Classification == Classification.Pass, "Plant.Code PASS");

        output.WriteLine("C5: case-sensitive SQL Server (measured) -> environment-determined EXPECTED DIFFERENCE (D-k11)");
        var cs = Evaluator.CodeCase(Provider.SqlServer, DbContextKind.Master, new("Tenant.Code", true, false, true, "server=Latin1_General_CS_AS, database=Latin1_General_CS_AS", false));
        var csPlant = Evaluator.CodeCase(Provider.SqlServer, DbContextKind.AppDb, new("Plant.Code", null, false, true, "database=Latin1_General_CS_AS", false));
        var misleadingName = Evaluator.CodeCase(Provider.SqlServer, DbContextKind.Master, new("Tenant.Code", true, false, true, "database=SomeCollation_CI_AS (name says CI)", true));
        Expect("C5", cs.Classification == Classification.ExpectedDifference && cs.Detail.Contains("environment-determined: this SQL Server collation is case-sensitive"),
            "Tenant.Code: environment-determined difference");
        Expect("C5", csPlant.Classification == Classification.ExpectedDifference && csPlant.Detail.Contains("DB-09"), "Plant.Code: DB-09 divergence recorded");
        Expect("C5", misleadingName.Classification == Classification.ExpectedDifference, "measured column behaviour wins over a collation NAME that says CI");
        var pgCs = Evaluator.CodeCase(Provider.PostgreSql, DbContextKind.AppDb, new("Plant.Code", null, false, true, "datcollate=en_US.utf8", false));
        var pgCi = Evaluator.CodeCase(Provider.PostgreSql, DbContextKind.Master, new("Tenant.Code", true, true, false, "nondeterministic ICU", true));
        Expect("C5", pgCs.Classification == Classification.ExpectedDifference && pgCs.Detail.Contains("KNOWN Plant.Code provider divergence"),
            "PostgreSQL default case-sensitive Plant.Code: DB-09 recorded");
        Expect("C5", pgCi.Classification == Classification.ExpectedDifference && pgCi.Detail.Contains("environment-determined"),
            "case-insensitive PostgreSQL column: environment-determined difference");
        Expect("C5", Evaluator.CodeCase(Provider.SqlServer, DbContextKind.Master, new("Tenant.Code", true, true, true)).Classification == Classification.FailDefect,
            "lookup/uniqueness disagreement -> DEFECT");

        output.WriteLine("C6: Tenant.Code application lookup failure -> FAIL — DEFECT regardless of collation");
        foreach (var (p, raw, duplicate, label) in new[]
                 {
                     (Provider.SqlServer, true, false, "CI SQL Server"), (Provider.SqlServer, false, true, "CS SQL Server"),
                     (Provider.PostgreSql, false, true, "CS PostgreSQL"), (Provider.PostgreSql, true, false, "CI PostgreSQL"),
                 })
        {
            var r = Evaluator.CodeCase(p, DbContextKind.Master, new("Tenant.Code", false, raw, duplicate, "synthetic", raw));
            Expect("C6", r.Classification == Classification.FailDefect && r.Detail.Contains("regardless of collation"), $"{label}: {Report.Label(r.Classification)}");
        }

        output.WriteLine("C7: credentials in every report collection -> redacted in console and JSON (Low-1)");
        var c7 = new Report(RunMode.Live);
        c7.Add(new CheckResult(Checks.L1, "title", "SqlServer/Master", Classification.FailDefect, "detail Password=SECRET-ONE;Server=db.example"));
        c7.Results.Add(new CheckResult(Checks.L2, "title Pwd=SECRET-TWO", "SqlServer/Master User Id=SECRET-THREE", Classification.FailDefect, "raw (bypassed Add) Password=SECRET-FOUR"));
        c7.Evidence.Add("evidence Server=db.example;User ID=SECRET-FIVE;Password=SECRET-SIX");
        c7.SafetyRefusals.Add("refusal Host=pg.example;Username=SECRET-SEVEN;Password=SECRET-EIGHT");
        c7.CleanupFailures.Add("cleanup Data Source=db.example;UID=SECRET-NINE;PWD=SECRET-TEN");
        var console = c7.Render();
        var json = c7.ToJson();
        Expect("C7", !console.Contains("SECRET-") && !json.Contains("SECRET-"), "no secret value in console or JSON");
        Expect("C7", console.Contains("Password=<redacted>") && json.Contains("redacted"), "redaction marker present");
        Expect("C7", console.Contains("Server=db.example") && console.Contains("Host=pg.example") && json.Contains("db.example"), "non-secret diagnostics preserved");

        output.WriteLine("C8: cleanup failure retains exit code 5");
        var c8 = new Report(RunMode.Live);
        c8.Add(Evaluator.Infrastructure(Checks.L1, Provider.SqlServer, DbContextKind.Master, "synthetic"));
        c8.Add(Evaluator.Failed(Checks.L2, Provider.SqlServer, DbContextKind.Master, "synthetic"));
        c8.SafetyRefusals.Add("synthetic refusal");
        c8.CleanupFailures.Add("synthetic cleanup failure");
        Expect("C8", c8.ExitCode == ExitCodes.CleanupFailure && c8.Verdict.Contains("CLEANUP FAILURE"), $"exit {c8.ExitCode} == 5 with infrastructure, defect and safety present");

        output.WriteLine("C9: AppDb L-10 remains DEFERRED; Master L-10 is reported per provider and never as PASS without a run");
        Expect("C9", l4.Results.Single(r => r.Scope == "All/AppDb").Classification == Classification.Deferred
                     && c1.Results.Single(r => r.Scope == "All/AppDb").Classification == Classification.Deferred,
            "AppDb L-10 DEFERRED in simulation and live paths");
        Expect("C9", new[] { "SqlServer/Master", "PostgreSql/Master" }.All(scope =>
                l4.Results.Single(r => r.Id == Checks.L10 && r.Scope == scope).Classification == Classification.Pass
                && c1.Results.Single(r => r.Id == Checks.L10 && r.Scope == scope).Classification == Classification.SkippedNoEndpoint),
            "Master L-10: one row per provider — simulated PASS in simulation, SKIPPED (not PASS) without an endpoint");

        output.WriteLine("C10: DB-03 remains PARTIALLY OPEN in every verdict");
        var refusal = new Report(RunMode.Live);
        refusal.SafetyRefusals.Add("synthetic");
        var verdicts = new[] { c1, c2, c3, c8, l4, refusal }.Select(r => r.Verdict).ToArray();
        Expect("C10", verdicts.All(v => v.Contains(db03)),
            $"all {verdicts.Length} verdict kinds (incomplete, infrastructure, defect, cleanup, simulated success, safety) say {db03}");

        // ── C11: L-9 IdentitySeeder harness correction — offline EF change tracking only ──
        output.WriteLine("C11: L-9 seeder context, captured seeder errors, redaction (offline; no connection)");
        var offline = new Dictionary<Provider, string>
        {
            // .invalid hosts: never resolvable, and every connection attempt is suppressed and counted below.
            [Provider.SqlServer] = "Server=parity-offline.invalid;Database=master;Trusted_Connection=true;TrustServerCertificate=true;Connect Timeout=1;",
            [Provider.PostgreSql] = "Host=parity-offline.invalid;Database=postgres;Username=nobody;Timeout=1",
        };
        static int Auditable(DbContextOptions options) =>
            options.FindExtension<CoreOptionsExtension>()?.Interceptors?.Count(i => i is AuditableEntityInterceptor) ?? 0;

        foreach (var p in new[] { Provider.SqlServer, Provider.PostgreSql })
        {
            var master = new LiveRunner.Scope(p, DbContextKind.Master, offline[p]);
            var appDb = new LiveRunner.Scope(p, DbContextKind.AppDb, offline[p]);

            // A — the seeder context carries exactly one AuditableEntityInterceptor.
            Expect("C11-A", Auditable(master.SeederOptions()) == 1, $"{p}: L-9 seeder context has AuditableEntityInterceptor");

            // B — every other probe context stays free of it.
            await using (var plainMaster = master.Master())
            await using (var plainApp = appDb.App())
            await using (var plainContext = appDb.Context())
            {
                Expect("C11-B", new DbContext[] { plainMaster, plainApp, plainContext }.All(c => Auditable(c.GetService<IDbContextOptions>() as DbContextOptions ?? throw new InvalidOperationException()) == 0),
                    $"{p}: Master/AppDb probe contexts have no AuditableEntityInterceptor");
            }

            // C — real SaveChangesAsync on the seeder options: the interceptor stamps the role,
            //     then a later test-only interceptor suppresses the save; no connection is attempted.
            var watch = new ConnectionWatch();
            var probeOptions = new DbContextOptionsBuilder<MasterDbContext>(master.SeederOptions())
                .AddInterceptors(new SuppressSave(), watch)
                .Options;
            var before = DateTime.UtcNow;
            var role = new AppRole { Name = "C11 Role", NormalizedName = "C11-ROLE" };
            await using (var seeder = new MasterDbContext(probeOptions))
            {
                seeder.Add(role);
                await seeder.SaveChangesAsync();
            }
            Expect("C11-C", role.CreatedAt != default && role.CreatedAt.Kind == DateTimeKind.Utc && role.CreatedAt >= before.AddSeconds(-1),
                $"{p}: added AppRole stamped with UTC CreatedAt ({role.CreatedAt:O})");
            Expect("C11-C", role.CreatedBy is null, $"{p}: CreatedBy is null (anonymous system actor)");
            Expect("C11-C", watch.Attempts == 0, $"{p}: zero connection attempts ({watch.Attempts})");
        }

        var fullCounts = new Dictionary<string, int>
        {
            ["Tenants"] = 1, ["Users"] = 4, ["Roles"] = 4, ["Permissions"] = 28, ["Modules"] = 3, ["Menus"] = 10,
            ["MenuPermissions"] = 12, ["RolePermissions"] = 60, ["UserRoles"] = 4, ["UserTenants"] = 4,
        };
        var observedPartial = new Dictionary<string, int>
        {
            ["Tenants"] = 1, ["Permissions"] = 28, ["Users"] = 0, ["Roles"] = 0, ["Modules"] = 0, ["Menus"] = 0,
            ["MenuPermissions"] = 0, ["RolePermissions"] = 0, ["UserRoles"] = 0, ["UserTenants"] = 0,
        };
        var fp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("c11-structure")));
        const string capturedUtcError = "Error: DbUpdateException <- InvalidOperationException: Refusing to persist a DateTime with Kind=Unspecified (0001-01-01T00:00:00.0000000).";

        // D — a captured seeder error fails L-9, and takes precedence over the count checks.
        var d = Evaluator.Seed(Provider.SqlServer, DbContextKind.Master, new SeedMeasurement(observedPartial, fp, true, [capturedUtcError]));
        Expect("C11-D", d.Classification == Classification.FailDefect && d.Detail.StartsWith("IdentitySeeder logged 1 error(s)", StringComparison.Ordinal),
            "captured seeder error -> FAIL — DEFECT, reported first");

        // E — redaction: in the capturing logger itself, and again at the report sinks.
        var log = new CapturingLogger<MasterDbContext>();
        Exception thrown;
        try { throw new DbUpdateException("outer", new InvalidOperationException("connect failed Password=SECRET-SEEDER-1;\nSECOND-LINE-DETAIL")); }
        catch (Exception ex) { thrown = ex; }
        log.LogError(thrown, "Error seeding identity data");
        log.LogCritical("critical Pwd=SECRET-SEEDER-2;");
        log.LogWarning(new Exception("warning-only"), "warning Password=SECRET-SEEDER-3");
        Expect("C11-E", log.Errors.Count == 2, $"Error and Critical captured, Warning ignored ({log.Errors.Count})");
        Expect("C11-E", log.Errors.All(e => !e.Contains("SECRET-SEEDER", StringComparison.Ordinal)), "captured entries redacted at capture time");
        Expect("C11-E", log.Errors[0].Contains("DbUpdateException <- InvalidOperationException") && !log.Errors.Any(e => e.Contains("SECOND-LINE") || e.Contains("   at ")),
            "exception types kept; first line only; no stack trace");
        var e7 = new Report(RunMode.Live);
        e7.Add(Evaluator.Seed(Provider.SqlServer, DbContextKind.Master, new SeedMeasurement(fullCounts, fp, true, ["Error: X: token Password=SECRET-SEEDER-4;"])));
        Expect("C11-E", !e7.Render().Contains("SECRET-SEEDER") && !e7.ToJson().Contains("SECRET-SEEDER"), "seeder diagnostic redacted in console and JSON");

        // F — the exact partial state observed in the SQL Server run fails even with no captured error.
        var f = Evaluator.Seed(Provider.SqlServer, DbContextKind.Master, new SeedMeasurement(observedPartial, fp, true));
        Expect("C11-F", f.Classification == Classification.FailDefect && f.Detail.Contains("[Users,Roles,Modules,Menus]"),
            "Tenants=1, Permissions=28, Users/Roles/Modules/Menus=0 -> FAIL — DEFECT");

        // G — complete-looking counts plus a captured error still fail.
        var g = Evaluator.Seed(Provider.PostgreSql, DbContextKind.Master, new SeedMeasurement(fullCounts, fp, true, [capturedUtcError]));
        Expect("C11-G", g.Classification == Classification.FailDefect, "complete counts + captured error -> FAIL — DEFECT");

        // H — idempotency and fingerprint checks unchanged.
        Expect("C11-H", Evaluator.Seed(Provider.SqlServer, DbContextKind.Master, new SeedMeasurement(fullCounts, fp, true, [])).Classification == Classification.Pass,
            "complete, idempotent, no errors -> PASS");
        Expect("C11-H", Evaluator.Seed(Provider.SqlServer, DbContextKind.Master, new SeedMeasurement(fullCounts, fp, false)).Classification == Classification.FailDefect,
            "not idempotent -> FAIL — DEFECT");
        var other = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("c11-other")));
        Expect("C11-H", Evaluator.SeedEquivalence(new SeedMeasurement(fullCounts, fp, true), new SeedMeasurement(fullCounts, fp, true)).Classification == Classification.Pass,
            "identical cross-provider fingerprints -> PASS");
        Expect("C11-H", Evaluator.SeedEquivalence(new SeedMeasurement(fullCounts, fp, true), new SeedMeasurement(fullCounts, other, true)).Classification == Classification.FailDefect,
            "different cross-provider fingerprints -> FAIL — DEFECT");

        // ── P2-a.2: Master L-10, endpoint locality, lifecycle ownership — offline only ──
        failures += await UpgradeControlsAsync(output);

        output.WriteLine(failures == 0 ? "SELF-TEST: all controls behaved as specified." : $"SELF-TEST: {failures} control assertion(s) FAILED.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// U1..U7 — Master L-10 classification, endpoint locality and the L-10 database lifecycle.
    /// Pure evaluation plus a FAKE lifecycle: no connection, no name resolution, no database.
    /// </summary>
    private static async Task<int> UpgradeControlsAsync(TextWriter output)
    {
        var failures = 0;
        void Expect(string control, bool condition, string what)
        {
            output.WriteLine($"  [{(condition ? "ok" : "FAILED")}] {control}: {what}");
            if (!condition) failures++;
        }

        var master = Expectations.Approved.Master;
        var u = master.Upgrade!;
        const DbContextKind k = DbContextKind.Master;

        output.WriteLine("U1: pinned upgrade migrations are the last two of each approved Master lineage");
        foreach (var p in new[] { Provider.SqlServer, Provider.PostgreSql })
            Expect("U1", master.Lineage(p)[^2] == u.From(p) && master.Lineage(p)[^1] == u.To(p), $"{p}: {u.From(p)} -> {u.To(p)}");
        Expect("U1", Expectations.Approved.AppDb.Upgrade is null, "AppDb has no pinned upgrade");

        output.WriteLine("U2: a complete, consistent upgrade cycle -> PASS (per provider)");
        foreach (var p in new[] { Provider.SqlServer, Provider.PostgreSql })
        {
            var r = Evaluator.Upgrade(p, k, Simulation.Upgrade(p, master), master);
            Expect("U2", r.Classification == Classification.Pass && r.Scope == $"{p}/Master" && r.Id == Checks.L10, $"{p}: {Report.Label(r.Classification)}");
        }

        output.WriteLine("U3: every injected fault -> FAIL — DEFECT, naming stage and object");
        var good = Simulation.Upgrade(Provider.PostgreSql, master);
        string Fp(string seed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();
        Dictionary<string, string> Tamper(string table) => good.Before.Fingerprints.ToDictionary(kv => kv.Key, kv => kv.Key == table ? Fp("tampered") : kv.Value);
        var faults = new (string Name, UpgradeMeasurement M, string Marker)[]
        {
            ("fingerprint differs after up", good with { AfterUp = good.AfterUp with { Fingerprints = Tamper("Tenants") } }, "after-up: Tenants fingerprint"),
            ("fingerprint differs after down", good with { AfterDown = good.AfterDown with { Fingerprints = Tamper("Users") } }, "after-down: Users fingerprint"),
            ("fingerprint differs after re-up", good with { AfterReUp = good.AfterReUp with { Fingerprints = Tamper("UserRoles") } }, "after-re-up: UserRoles fingerprint"),
            ("fixture table not fingerprinted", good with { Before = good.Before with { Fingerprints = good.Before.Fingerprints.Where(kv => kv.Key != "Roles").ToDictionary() } }, "before: fingerprinted tables"),
            ("non-NULL CompanyId after up", good with { AfterUp = good.AfterUp with { TenantsWithCompany = 1 } }, "after-up: 1 tenant(s) with non-NULL CompanyId"),
            ("Companies not empty", good with { AfterReUp = good.AfterReUp with { CompaniesRows = 2 } }, "after-re-up: Companies has 2 row(s)"),
            ("wrong history after up", good with { AfterUp = good.AfterUp with { History = good.Before.History } }, "after-up: history"),
            ("wrong history after down", good with { AfterDown = good.AfterDown with { History = good.AfterUp.History } }, "after-down: history"),
            ("Companies missing after re-up", good with { AfterReUp = good.AfterReUp with { Shape = CompanyShape.Absent } }, "after-re-up: Companies table missing"),
            ("nullable Companies column", good with { AfterUp = good.AfterUp with { Shape = good.AfterUp.Shape with { NullableCompanyColumns = ["Code"] } } }, "nullable Companies columns [Code]"),
            ("CompanyId NOT NULL", good with { AfterUp = good.AfterUp with { Shape = good.AfterUp.Shape with { TenantCompanyIdNullable = false } } }, "Tenants.CompanyId is NOT NULL"),
            ("code index not unique", good with { AfterUp = good.AfterUp with { Shape = good.AfterUp.Shape with { CodeIndexUnique = false } } }, "IX_Companies_Code unique=False"),
            ("FK cascades", good with { AfterUp = good.AfterUp with { Shape = good.AfterUp.Shape with { ForeignKeyDeleteAction = "CASCADE" } } }, "delete action 'CASCADE'"),
            ("schema wrong after up", good with { AfterUp = good.AfterUp with { Schema = good.AfterUp.Schema with { ForeignKeys = 17 } } }, "after-up: schema"),
            ("Company table left after down", good with { AfterDown = good.AfterDown with { Shape = good.AfterDown.Shape with { CompaniesTable = true } } }, "after-down: Companies table present"),
            ("CompanyId left after down", good with { AfterDown = good.AfterDown with { Shape = good.AfterDown.Shape with { TenantCompanyIdColumn = true } } }, "after-down: Tenants.CompanyId present"),
            ("schema not restored after down", good with { AfterDown = good.AfterDown with { Schema = good.AfterUp.Schema } }, "after-down: schema differs"),
            ("FK probe accepted", good with { Probes = good.Probes.Select(x => x.Name == "invalid-company-fk" ? x with { Rejected = false } : x).ToList() }, "probe invalid-company-fk: not rejected"),
            ("restrict probe accepted", good with { Probes = good.Probes.Select(x => x.Name == "restrict-delete" ? x with { Rejected = false } : x).ToList() }, "probe restrict-delete: not rejected"),
            ("unique probe accepted", good with { Probes = good.Probes.Select(x => x.Name == "duplicate-code" ? x with { Rejected = false } : x).ToList() }, "probe duplicate-code: not rejected"),
            ("probe rollback unconfirmed", good with { Probes = good.Probes.Select(x => x.Name == "restrict-delete" ? x with { RolledBack = false } : x).ToList() }, "probe restrict-delete: rollback not confirmed"),
            ("probe missing", good with { Probes = good.Probes.Where(x => x.Name != "duplicate-code").ToList() }, "probe duplicate-code: not run"),
            ("fixture rows short", good with { FixtureRows = 10 }, "fixture rows 10 != 11"),
        };
        foreach (var (name, m, marker) in faults)
        {
            var r = Evaluator.Upgrade(Provider.PostgreSql, k, m, master);
            Expect("U3", r.Classification == Classification.FailDefect && r.Detail.Contains(marker, StringComparison.Ordinal), $"{name} -> {Report.Label(r.Classification)}");
        }
        var inconsistent = master with { Upgrade = u with { PostgreSqlTo = "29990101000000_NotTheNextMigration" } };
        Expect("U3", Evaluator.Upgrade(Provider.PostgreSql, k, good, inconsistent).Detail.Contains("expectations inconsistent"), "pins not adjacent in the lineage -> FAIL");
        Expect("U3", Evaluator.Upgrade(Provider.PostgreSql, k, good, Expectations.Approved.AppDb).Classification == Classification.FailDefect, "no pinned upgrade -> FAIL");

        output.WriteLine("U4: success verdict names only providers with an actual Master L-10 PASS");
        var live = new Report(RunMode.Live);
        live.Add(Evaluator.Upgrade(Provider.SqlServer, k, Simulation.Upgrade(Provider.SqlServer, master), master));
        live.Add(Evaluator.Upgrade(Provider.PostgreSql, k, Simulation.Upgrade(Provider.PostgreSql, master), master));
        live.Add(Evaluator.DeferredAppDb());
        Expect("U4", live.ExitCode == ExitCodes.Success && live.Verdict.Contains("Master L-10 verified live on [PostgreSql, SqlServer]")
                     && live.Verdict.Contains("AppDb L-10 DEFERRED") && live.Verdict.Contains("DB-03 PARTIALLY OPEN"), live.Verdict);
        var partial = new Report(RunMode.Live);
        partial.Add(Evaluator.Upgrade(Provider.SqlServer, k, Simulation.Upgrade(Provider.SqlServer, master), master));
        partial.Add(Evaluator.Skipped(Checks.L10, Provider.PostgreSql, k, "no endpoint"));
        partial.Add(Evaluator.DeferredAppDb());
        Expect("U4", partial.ExitCode == ExitCodes.Incomplete && !partial.Verdict.Contains("verified live"), "a SKIPPED provider is never reported verified (exit 2)");
        var deferredOnly = new Report(RunMode.Live);
        deferredOnly.Add(Evaluator.DeferredAppDb());
        Expect("U4", deferredOnly.Results.Single() is { Scope: "All/AppDb", Classification: Classification.Deferred } && deferredOnly.Count(Classification.Pass) == 0,
            "AppDb L-10 is DEFERRED, never PASS");

        output.WriteLine("U5: endpoint locality — accepted only on an exact, loopback, owner-approved match (pure parsing)");
        string? Locality(Provider p, string raw, string? expected)
        {
            try { return EndpointLocality.Validate(p, raw, expected); }
            catch (SafetyRefusalException) { return null; }
        }
        const string sa = "User ID=nobody;Password=U5-SECRET;TrustServerCertificate=true;";
        const string pa = "Username=nobody;Password=U5-SECRET;";
        Expect("U5", Locality(Provider.SqlServer, "Server=localhost,14333;" + sa, "localhost,14333") == "localhost,14333", "SQL Server host,port exact match accepted");
        Expect("U5", Locality(Provider.SqlServer, @"Server=LocalHost\SqlDev;" + sa, @"localhost\SQLDEV") == @"localhost\SQLDEV", "SQL Server named instance accepted (case-normalized)");
        Expect("U5", Locality(Provider.PostgreSql, "Host=localhost;Port=5433;" + pa, "localhost:5433") == "localhost:5433", "PostgreSQL host:port exact match accepted");
        Expect("U5", Locality(Provider.PostgreSql, "Host=::1;Port=5433;" + pa, "[::1]:5433") == "[::1]:5433", "PostgreSQL IPv6 loopback accepted");
        var refused = new (Provider P, string Raw, string? Expected, string Why)[]
        {
            (Provider.SqlServer, "Server=localhost,14333;" + sa, null, "expected endpoint not set"),
            (Provider.SqlServer, "Server=localhost,14333;" + sa, "   ", "expected endpoint blank"),
            (Provider.SqlServer, "Server=localhost,14334;" + sa, "localhost,14333", "different port"),
            (Provider.SqlServer, "Server=localhost;" + sa, "localhost,14333", "port omitted"),
            (Provider.SqlServer, "Server=127.0.0.1,14333;" + sa, "localhost,14333", "equivalent-looking host not treated as equal"),
            (Provider.SqlServer, @"Server=localhost\OTHER;" + sa, @"localhost\SQLDEV", "different instance"),
            (Provider.SqlServer, "Server=db.example.com,1433;" + sa, "db.example.com,1433", "remote host (even when expected says so)"),
            (Provider.SqlServer, "Server=203.0.113.5,1433;" + sa, "localhost,1433", "public IP"),
            (Provider.SqlServer, "Server=tcp:localhost,1433;" + sa, "localhost,1433", "protocol prefix"),
            (Provider.SqlServer, @"Server=np:\\.\pipe\sql\query;" + sa, "localhost", "named pipe"),
            (Provider.SqlServer, @"Server=(localdb)\MSSQLLocalDB;" + sa, "localhost", "LocalDB"),
            (Provider.SqlServer, "Server=localhost,1433;Failover Partner=localhost,1434;" + sa, "localhost,1433", "failover partner"),
            (Provider.SqlServer, "Server=localhost,1433;MultiSubnetFailover=true;" + sa, "localhost,1433", "MultiSubnetFailover"),
            (Provider.SqlServer, @"Server=localhost,1433;AttachDbFilename=C:\x.mdf;" + sa, "localhost,1433", "AttachDBFilename"),
            (Provider.SqlServer, "Server=localhost,1,2;" + sa, "localhost,1,2", "ambiguous data source"),
            (Provider.SqlServer, "Server=localhost,99999;" + sa, "localhost,99999", "invalid port"),
            (Provider.SqlServer, "this is not a connection string", "localhost,1433", "unparseable connection string"),
            (Provider.SqlServer, "Server=localhost,1433;NotARealKeyword=1;" + sa, "localhost,1433", "unknown keyword"),
            (Provider.PostgreSql, "Host=localhost;Port=5433;" + pa, null, "expected endpoint not set"),
            (Provider.PostgreSql, "Host=localhost;" + pa, "localhost:5433", "default port differs from approved port"),
            (Provider.PostgreSql, "Host=localhost,127.0.0.1;Port=5433;" + pa, "localhost:5433", "multiple hosts"),
            (Provider.PostgreSql, "Host=/var/run/postgresql;Port=5433;" + pa, "localhost:5433", "Unix socket"),
            (Provider.PostgreSql, "Host=localhost:5433;" + pa, "localhost:5433", "port inside Host"),
            (Provider.PostgreSql, "Host=pg.example.com;Port=5432;" + pa, "pg.example.com:5432", "remote host"),
            (Provider.PostgreSql, "Host=localhost;Port=5433;Load Balance Hosts=true;" + pa, "localhost:5433", "load balancing"),
            (Provider.PostgreSql, "Host=localhost;Port=5433;Target Session Attributes=primary;" + pa, "localhost:5433", "target session attributes"),
            (Provider.PostgreSql, "Host=localhost;Port=5433;" + pa, "localhost", "malformed expected value"),
            (Provider.PostgreSql, "Host=127.0.0.1;Port=5433;" + pa, "localhost:5433", "equivalent-looking host not treated as equal"),
        };
        foreach (var (p, raw, expected, why) in refused)
            Expect("U5", Locality(p, raw, expected) is null, $"{p}: {why} -> refused");
        var leaked = new List<string>();
        foreach (var (p, raw, expected, _) in refused)
        {
            try { EndpointLocality.Validate(p, raw, expected); }
            catch (SafetyRefusalException ex) when (ex.Message.Contains("U5-SECRET") || ex.Message.Contains("nobody")) { leaked.Add(ex.Message); }
            catch (SafetyRefusalException) { }
        }
        Expect("U5", leaked.Count == 0, "no refusal message contains a credential or user name");
        Expect("U5", Throws(() => Endpoint.Resolve(Provider.SqlServer, new Dictionary<string, string?>
            { [Endpoint.SqlServerVariable] = "Server=localhost,14333;" + sa }).ValidateLocality()), "Endpoint without the expected variable refused");
        Expect("U5", !Throws(() => Endpoint.Resolve(Provider.PostgreSql, new Dictionary<string, string?>
            { [Endpoint.PostgreSqlVariable] = "Host=localhost;Port=5433;" + pa, [Endpoint.PostgreSqlExpectedVariable] = "localhost:5433" }).ValidateLocality()),
            "Endpoint with a matching expected variable accepted");

        output.WriteLine("U6: L-10 lifecycle ownership (fake lifecycle — no database)");
        Task<UpgradeMeasurement> Passing(UpgradeProgress progress, CancellationToken _)
        {
            progress.Stage = "fake measurement";
            return Task.FromResult(Simulation.Upgrade(Provider.SqlServer, master));
        }
        async Task<(Report Report, FakeLifecycle Fake)> Cycle(FakeLifecycle fake, Func<UpgradeProgress, CancellationToken, Task<UpgradeMeasurement>>? measure = null)
        {
            var report = new Report(RunMode.Live);
            await LiveRunner.RunMasterUpgradeCycleAsync(Provider.SqlServer, fake, measure ?? Passing, master, report, CancellationToken.None);
            return (report, fake);
        }
        CheckResult L10(Report r) => r.Results.Single(x => x.Id == Checks.L10);

        var (ok, okFake) = await Cycle(new FakeLifecycle());
        Expect("U6", L10(ok).Classification == Classification.Pass && ok.ExitCode == ExitCodes.Success, "complete cycle -> PASS, exit 0");
        Expect("U6", okFake.Calls.SequenceEqual(["exists", "create", "drop", "exists"]) && !okFake.Present, $"exists, create, drop, verify-absent ({string.Join(",", okFake.Calls)})");
        Expect("U6", okFake.Names.All(n => n == Safety.SqlServerDatabase), "only the approved name is ever used");

        var (existing, existingFake) = await Cycle(new FakeLifecycle { Present = true });
        Expect("U6", existing.SafetyRefusals.Count == 1 && existing.ExitCode == ExitCodes.SafetyRefusal && L10(existing).Detail.Contains("already exists"),
            "pre-existing database -> SAFETY REFUSAL, exit 4");
        Expect("U6", !existingFake.Calls.Contains("create") && !existingFake.Calls.Contains("drop") && existingFake.Present, "pre-existing database neither created over nor dropped");

        var (createFailed, createFailedFake) = await Cycle(new FakeLifecycle { FailCreate = true });
        Expect("U6", L10(createFailed).Classification == Classification.FailDefect && L10(createFailed).Detail.Contains("stage 'create database'")
                     && createFailed.CleanupFailures.Count == 0 && createFailed.ExitCode == ExitCodes.Failure, "failed CREATE leaving nothing -> FAIL, exit 1");
        Expect("U6", !createFailedFake.Calls.Contains("drop"), "nothing dropped after a failed CREATE");

        var (ambiguous, ambiguousFake) = await Cycle(new FakeLifecycle { FailCreate = true, CreateLeavesDatabase = true });
        Expect("U6", ambiguous.CleanupFailures.Count == 1 && ambiguous.CleanupFailures[0].Contains("UNVERIFIED") && ambiguous.ExitCode == ExitCodes.CleanupFailure,
            "failed CREATE that left a database -> ownership UNVERIFIED, exit 5 (hard stop)");
        Expect("U6", !ambiguousFake.Calls.Contains("drop") && ambiguousFake.Present, "a database of unverified ownership is never dropped");

        var (interrupted, interruptedFake) = await Cycle(new FakeLifecycle(), (progress, _) =>
        {
            progress.Stage = "migrate down to 20261007022731_InitialMasterBaseline";
            throw new InvalidOperationException("synthetic interruption");
        });
        Expect("U6", L10(interrupted).Classification == Classification.FailDefect && L10(interrupted).Detail.Contains("stage 'migrate down to")
                     && interrupted.ExitCode == ExitCodes.Failure, "failure mid-cycle -> FAIL naming the stage, exit 1");
        Expect("U6", interruptedFake.Calls.Count(c => c == "drop") == 1 && !interruptedFake.Present, "database created by this run still dropped and verified absent");

        var (infra, _) = await Cycle(new FakeLifecycle(), (_, _) => throw new TimeoutException("synthetic"));
        Expect("U6", L10(infra).Classification == Classification.FailInfrastructure, "environmental failure mid-cycle -> FAIL — INFRASTRUCTURE");

        var (defect, defectFake) = await Cycle(new FakeLifecycle(), (_, _) =>
        {
            var m = Simulation.Upgrade(Provider.SqlServer, master);
            return Task.FromResult(m with { AfterDown = m.AfterDown with { Fingerprints = Tamper("Tenants") } });
        });
        Expect("U6", L10(defect).Classification == Classification.FailDefect && L10(defect).Detail.Contains("after-down: Tenants fingerprint")
                     && defectFake.Calls.Contains("drop"), "measured defect -> FAIL — DEFECT; database still dropped");

        var (dropFailed, dropFailedFake) = await Cycle(new FakeLifecycle { FailDrop = true });
        Expect("U6", dropFailed.CleanupFailures.Count == 1 && dropFailed.ExitCode == ExitCodes.CleanupFailure
                     && L10(dropFailed).Classification == Classification.FailInfrastructure && L10(dropFailed).Detail.Contains("could not be removed"),
            "failed DROP -> cleanup failure, exit 5; a PASS is downgraded, never reported");
        Expect("U6", dropFailedFake.Present, "failed DROP leaves the state reported, not hidden");

        var (lingering, _) = await Cycle(new FakeLifecycle { DropLeavesDatabase = true });
        Expect("U6", lingering.CleanupFailures.Count == 1 && lingering.ExitCode == ExitCodes.CleanupFailure && L10(lingering).Classification != Classification.Pass,
            "DROP that does not remove the database -> cleanup failure, exit 5, no PASS");

        var (unknown, unknownFake) = await Cycle(new FakeLifecycle { FailExists = true });
        Expect("U6", L10(unknown).Classification != Classification.Pass && !unknownFake.Calls.Contains("create"), "existence cannot be established -> not run, nothing created");

        var cancelledFake = new FakeLifecycle();
        var cancelledReport = new Report(RunMode.Live);
        var cancelledThrown = false;
        try
        {
            await LiveRunner.RunMasterUpgradeCycleAsync(Provider.SqlServer, cancelledFake,
                (_, _) => throw new OperationCanceledException("synthetic cancel"), master, cancelledReport, CancellationToken.None);
        }
        catch (OperationCanceledException) { cancelledThrown = true; }
        Expect("U6", cancelledThrown && cancelledFake.Calls.Contains("drop") && !cancelledFake.Present && cancelledReport.Results.Count == 0,
            "operator cancellation -> propagated; owned database still dropped; no L-10 result claimed");

        output.WriteLine("U8: F1 — interrupted CREATE: ownership checked on a bounded token, never dropped, never assumed");
        async Task<(Report Report, FakeLifecycle Fake, bool Cancelled)> Interrupted(FakeLifecycle fake)
        {
            var report = new Report(RunMode.Live);
            var cancelled = false;
            try { await LiveRunner.RunMasterUpgradeCycleAsync(Provider.SqlServer, fake, Passing, master, report, CancellationToken.None); }
            catch (OperationCanceledException) { cancelled = true; }
            return (report, fake, cancelled);
        }

        var (cAbsent, cAbsentFake, cAbsentThrown) = await Interrupted(new FakeLifecycle { CancelCreate = true });
        Expect("U8", cAbsentThrown && cAbsent.CleanupFailures.Count == 0 && cAbsent.Results.Count == 0
                     && cAbsent.Evidence.Any(x => x.Contains("was cancelled") && x.Contains("confirmed absent")),
            "cancelled CREATE, database absent -> cancellation propagates; recorded absent; no cleanup failure; no L-10 result claimed");
        Expect("U8", !cAbsentFake.Calls.Contains("drop") && !cAbsent.Evidence.Any(x => x.Contains("owned by this run")), "nothing dropped; nothing described as owned");

        var (cPresent, cPresentFake, cPresentThrown) = await Interrupted(new FakeLifecycle { CancelCreate = true, CreateLeavesDatabase = true });
        Expect("U8", cPresentThrown && cPresent.CleanupFailures.Count == 1 && cPresent.CleanupFailures[0].Contains("was cancelled")
                     && cPresent.CleanupFailures[0].Contains("UNVERIFIED") && cPresent.ExitCode == ExitCodes.CleanupFailure,
            "cancelled CREATE, database exists -> ownership UNVERIFIED, exit 5");
        Expect("U8", !cPresentFake.Calls.Contains("drop") && cPresentFake.Present, "the database of unverified ownership is NOT dropped");

        var (cUnknown, cUnknownFake, cUnknownThrown) = await Interrupted(new FakeLifecycle { CancelCreate = true, FailExistsAfterCreate = true });
        Expect("U8", cUnknownThrown && cUnknown.CleanupFailures.Count == 1 && cUnknown.CleanupFailures[0].Contains("could not be determined")
                     && cUnknown.ExitCode == ExitCodes.CleanupFailure && !cUnknownFake.Calls.Contains("drop"),
            "cancelled CREATE, existence check fails -> inconclusive = UNVERIFIED, exit 5, no DROP");

        var (fUnknown, fUnknownFake, _) = await Interrupted(new FakeLifecycle { FailCreate = true, FailExistsAfterCreate = true });
        Expect("U8", fUnknown.CleanupFailures.Count == 1 && fUnknown.CleanupFailures[0].Contains("failed (") && fUnknown.ExitCode == ExitCodes.CleanupFailure
                     && L10(fUnknown).Classification == Classification.FailDefect && !fUnknownFake.Calls.Contains("drop"),
            "failed CREATE, existence check fails -> L-10 FAIL plus UNVERIFIED cleanup, exit 5, no DROP");

        var savedTimeout = LiveRunner.OwnershipCheckTimeout;
        try
        {
            LiveRunner.OwnershipCheckTimeout = TimeSpan.FromMilliseconds(50);
            var (hung, hungFake, hungThrown) = await Interrupted(new FakeLifecycle { CancelCreate = true, HangExistsAfterCreate = true });
            Expect("U8", hungThrown && hung.CleanupFailures.Count == 1 && hung.CleanupFailures[0].Contains("could not be determined")
                         && hung.ExitCode == ExitCodes.CleanupFailure && !hungFake.Calls.Contains("drop"),
                "existence check exceeding its bound -> inconclusive = UNVERIFIED, exit 5, no DROP");
        }
        finally { LiveRunner.OwnershipCheckTimeout = savedTimeout; }

        var tokenFake = new FakeLifecycle { CancelCreate = true };
        using (var runCancel = new CancellationTokenSource())
        {
            runCancel.Cancel();
            var report = new Report(RunMode.Live);
            try { await LiveRunner.RunMasterUpgradeCycleAsync(Provider.SqlServer, tokenFake, Passing, master, report, runCancel.Token); }
            catch (OperationCanceledException) { }
            Expect("U8", tokenFake.TokensAfterCreate.Count > 0 && tokenFake.TokensAfterCreate.All(t => !t.IsCancellationRequested && t.CanBeCanceled),
                "post-interruption check uses its own bounded token, unaffected by the run's cancellation");
        }

        Expect("U8", cancelledFake.Calls.SequenceEqual(["exists", "create", "drop", "exists"]),
            "cancellation AFTER a confirmed CREATE -> the owned database is dropped and verified absent (U6 cancellation path)");

        Expect("U8", L10(createFailed).Classification == Classification.FailDefect && createFailed.CleanupFailures.Count == 0
                     && createFailed.Evidence.Any(x => x.Contains("failed (") && x.Contains("confirmed absent")),
            "failed CREATE, database absent -> FAIL (exit 1), recorded absent, not an owned leftover");

        output.WriteLine("U9: F2 — exact persisted fixture rows per table at the Phase 1 head");
        Expect("U9", Evaluator.UpgradeFixtureCounts.Keys.Order(StringComparer.Ordinal).SequenceEqual(u.FixtureTables.Order(StringComparer.Ordinal))
                     && Evaluator.UpgradeFixtureCounts.Values.Sum() == u.FixtureRows,
            "pinned counts cover exactly the fixture tables and sum to the fixture row total (2/2/3/2/2 = 11)");
        Expect("U9", Evaluator.Upgrade(Provider.SqlServer, k, Simulation.Upgrade(Provider.SqlServer, master), master).Classification == Classification.Pass,
            "exact counts -> PASS");
        Dictionary<string, long> Counts(string table, long value) =>
            Evaluator.UpgradeFixtureCounts.ToDictionary(kv => kv.Key, kv => kv.Key == table ? value : kv.Value);
        var countFaults = new (string Name, IReadOnlyDictionary<string, long> Counts, string Marker)[]
        {
            ("missing Users row", Counts("Users", 1), "before: Users has 1 persisted row(s), expected 2"),
            ("extra UserTenants row", Counts("UserTenants", 4), "before: UserTenants has 4 persisted row(s), expected 3"),
            ("empty Tenants", Counts("Tenants", 0), "before: Tenants has 0 persisted row(s), expected 2"),
            ("Roles not counted", Evaluator.UpgradeFixtureCounts.Where(kv => kv.Key != "Roles").ToDictionary(), "before: Roles persisted row count not measured"),
        };
        foreach (var (name, counts, marker) in countFaults)
        {
            var r = Evaluator.Upgrade(Provider.SqlServer, k, Simulation.Upgrade(Provider.SqlServer, master) with { BeforeRowCounts = counts }, master);
            Expect("U9", r.Classification == Classification.FailDefect && r.Detail.Contains(marker, StringComparison.Ordinal), $"{name} -> FAIL naming the table");
        }

        output.WriteLine("U7: canonical fingerprint values are provider-neutral");
        var t = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
        Expect("U7", LiveRunner.Canonical(t) == LiveRunner.Canonical(t.AddTicks(-7)) && LiveRunner.Canonical(t) == LiveRunner.Canonical(new DateTimeOffset(t)),
            "100 ns vs 1 µs precision and DateTime vs DateTimeOffset do not register");
        Expect("U7", LiveRunner.Canonical(true) == "b:1" && LiveRunner.Canonical(null) == LiveRunner.Canonical(DBNull.Value)
                     && LiveRunner.Canonical(5) == LiveRunner.Canonical(5L) && LiveRunner.Canonical(Guid.Empty) == "g:00000000-0000-0000-0000-000000000000",
            "bool, NULL, integer width and GUID forms are normalized");
        Expect("U7", LiveRunner.Canonical("ab") != LiveRunner.Canonical("a") + LiveRunner.Canonical("b") && LiveRunner.Canonical(t) != LiveRunner.Canonical(t.AddTicks(10)),
            "strings are length-prefixed; a whole-microsecond change still registers");

        return failures;
    }

    /// <summary>
    /// Test-only lifecycle: an in-memory "does the database exist" flag and a call log. It can
    /// fail any step. It never touches a server.
    /// </summary>
    private sealed class FakeLifecycle : IDatabaseLifecycle
    {
        public bool Present { get; set; }
        public bool FailCreate { get; init; }
        public bool CreateLeavesDatabase { get; init; }
        public bool FailDrop { get; init; }
        public bool DropLeavesDatabase { get; init; }
        public bool FailExists { get; init; }
        public bool CancelCreate { get; init; }
        public bool FailExistsAfterCreate { get; init; }
        public bool HangExistsAfterCreate { get; init; }
        public List<string> Calls { get; } = [];
        public List<string> Names { get; } = [];
        public List<CancellationToken> TokensAfterCreate { get; } = [];
        private bool _createAttempted;

        public async Task<bool> ExistsAsync(string database, CancellationToken ct)
        {
            Calls.Add("exists");
            Names.Add(database);
            if (_createAttempted) TokensAfterCreate.Add(ct);
            if (FailExists || (_createAttempted && FailExistsAfterCreate))
                throw new InvalidOperationException("synthetic existence failure");
            if (_createAttempted && HangExistsAfterCreate)
                await Task.Delay(Timeout.Infinite, ct); // only the bounded token can end this
            return Present;
        }

        public Task CreateAsync(string database, CancellationToken ct)
        {
            Calls.Add("create");
            Names.Add(database);
            _createAttempted = true;
            if (CancelCreate)
            {
                Present = CreateLeavesDatabase;
                return Task.FromException(new OperationCanceledException("synthetic cancellation during CREATE"));
            }
            if (FailCreate)
            {
                Present = CreateLeavesDatabase;
                return Task.FromException(new InvalidOperationException("synthetic CREATE failure"));
            }

            Present = true;
            return Task.CompletedTask;
        }

        public Task DropAsync(string database, CancellationToken ct)
        {
            Calls.Add("drop");
            Names.Add(database);
            if (FailDrop) return Task.FromException(new InvalidOperationException("synthetic DROP failure"));
            Present = DropLeavesDatabase;
            return Task.CompletedTask;
        }
    }

    /// <summary>Test-only: lets SaveChanges run its interceptors, then skips the database entirely.</summary>
    private sealed class SuppressSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            InterceptionResult<int>.SuppressWithResult(0);
    }

    /// <summary>Test-only: counts and SUPPRESSES any connection opening, so none can happen.</summary>
    private sealed class ConnectionWatch : DbConnectionInterceptor
    {
        private int _attempts;

        public int Attempts => _attempts;

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Interlocked.Increment(ref _attempts);
            return InterceptionResult.Suppress();
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            return ValueTask.FromResult(InterceptionResult.Suppress());
        }
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (SafetyRefusalException) { return true; }
    }
}
