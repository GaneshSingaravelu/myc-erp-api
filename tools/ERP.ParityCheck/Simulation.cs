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
                        _ => throw new InvalidOperationException(id),
                    });
                }
            }
        }

        report.Add(Evaluator.SeedEquivalence(seeds.GetValueOrDefault(Provider.SqlServer), seeds.GetValueOrDefault(Provider.PostgreSql)));
        report.Add(Evaluator.Deferred());
        return report;
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
        Expect("PC-L1a", l1a.Count(Classification.SkippedNoEndpoint) == matrix - 1 && l1a.Count(Classification.Deferred) == 1, "every check SKIPPED except L-10 DEFERRED");
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
        Expect("PC-L4", l4.Count(Classification.Deferred) == 1 && l4.Results.Single(r => r.Classification == Classification.Deferred).Id == Checks.L10, "L-10 explicitly DEFERRED");
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
        Expect("C1", c1.Results.Where(r => r.Id != Checks.L10).All(r => r.Classification == Classification.SkippedNoEndpoint), "every check SKIPPED");
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

        output.WriteLine("C9: L-10 remains DEFERRED");
        Expect("C9", l4.Results.Single(r => r.Id == Checks.L10).Classification == Classification.Deferred
                     && c1.Results.Single(r => r.Id == Checks.L10).Classification == Classification.Deferred, "DEFERRED in simulation and live paths");

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

        output.WriteLine(failures == 0 ? "SELF-TEST: all controls behaved as specified." : $"SELF-TEST: {failures} control assertion(s) FAILED.");
        return failures == 0 ? 0 : 1;
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
