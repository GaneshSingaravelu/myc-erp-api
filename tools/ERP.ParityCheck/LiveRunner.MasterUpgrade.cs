using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP.ParityCheck;

/// <summary>
/// The only database-lifecycle operations the L-10 cycle needs. The live implementation talks
/// to the configured server; the self-test supplies a fake so that every lifecycle outcome —
/// existing target, failed create, interrupted measurement, failed or unverified cleanup — is
/// exercised offline without a connection.
/// </summary>
public interface IDatabaseLifecycle
{
    Task<bool> ExistsAsync(string database, CancellationToken ct);

    Task CreateAsync(string database, CancellationToken ct);

    Task DropAsync(string database, CancellationToken ct);
}

/// <summary>Which L-10 stage is running, so a failure or interruption names where it happened.</summary>
public sealed class UpgradeProgress
{
    public string Stage { get; set; } = "not started";
}

/// <summary>
/// L-10 (Master) — INCREMENTAL UPGRADE, live. Its own create -> verify -> drop cycle:
///
///   1. migrate an empty approved throwaway database to the Phase 1 head only;
///   2. insert deterministic synthetic rows (parameterized raw SQL — the current EF model
///      already contains CompanyId, so it cannot write to the Phase 1 schema);
///   3. fingerprint the ORIGINAL columns of every fixture table;
///   4. migrate up to the pinned upgrade; verify schema, Company objects, NULL CompanyId, data;
///   5. run the constraint probes, each in a transaction that is rolled back and then
///      confirmed rolled back from a separate connection;
///   6. migrate down to the Phase 1 head; verify the schema and data are as before;
///   7. migrate up again; verify everything again;
///   8. drop the database — only because this run created it — and verify it is gone.
///
/// Nothing here runs unless the live runner was started with --live and every guard passed.
/// </summary>
public static partial class LiveRunner
{
    // ── Live entry point ─────────────────────────────────────────────────────

    private static Task RunMasterUpgradeAsync(Provider p, Endpoint endpoint, ContextExpectations e, Report report, CancellationToken ct) =>
        RunMasterUpgradeCycleAsync(
            p,
            new ServerLifecycle(p, endpoint.AdminConnectionString()),
            (progress, token) => MeasureUpgradeAsync(new Scope(p, DbContextKind.Master, endpoint.TargetConnectionString()), e, progress, token),
            e, report, ct);

    /// <summary>
    /// Lifecycle orchestration, independent of how the database is reached. Always reports
    /// exactly one Master L-10 result for <paramref name="p"/> (unless cancelled). Ownership
    /// rules: an existing target is a safety refusal and is never touched; only a database this
    /// call created is dropped; if a failed create leaves ownership unknown, nothing is dropped
    /// and a cleanup failure (exit 5) forces a hard stop.
    /// </summary>
    internal static async Task RunMasterUpgradeCycleAsync(
        Provider p,
        IDatabaseLifecycle lifecycle,
        Func<UpgradeProgress, CancellationToken, Task<UpgradeMeasurement>> measure,
        ContextExpectations e,
        Report report,
        CancellationToken ct)
    {
        const DbContextKind k = DbContextKind.Master;
        var database = Safety.ApprovedDatabase(p);
        var progress = new UpgradeProgress();

        try
        {
            Safety.AssertApprovedTarget(p, database);
        }
        catch (SafetyRefusalException refusal)
        {
            report.SafetyRefusals.Add(refusal.Message);
            report.Add(Evaluator.Failed(Checks.L10, p, k, "not run: safety refusal"));
            return;
        }

        bool exists;
        try
        {
            exists = await lifecycle.ExistsAsync(database, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = $"not run: could not establish whether '{database}' exists: {Describe(ex)}";
            report.Add(IsInfrastructureFailure(ex) ? Evaluator.Infrastructure(Checks.L10, p, k, reason) : Evaluator.Failed(Checks.L10, p, k, reason));
            return;
        }

        if (exists)
        {
            report.SafetyRefusals.Add(
                $"{p}: '{database}' already exists. L-10 refuses to reuse or overwrite it; it was NOT created by this run and will NOT be dropped.");
            report.Add(Evaluator.Failed(Checks.L10, p, k, "not run: safety refusal (target database already exists)"));
            return;
        }

        var created = false;
        CheckResult? result = null;
        try
        {
            progress.Stage = "create database";
            try
            {
                await lifecycle.CreateAsync(database, ct);
            }
            catch (Exception interruption)
            {
                // F1: failed OR cancelled — the server may still have completed the CREATE.
                // Record the ownership outcome, then let the original exception (incl. cancellation) continue.
                await RecordOwnershipAfterInterruptedCreateAsync(
                    p, database, token => lifecycle.ExistsAsync(database, token), report, "Master L-10", interruption);
                throw;
            }
            created = true;
            report.Evidence.Add($"{p}/Master L-10: created disposable database '{database}' (owned by this run)");

            var m = await measure(progress, ct);
            progress.Stage = "evaluate";
            result = Evaluator.Upgrade(p, k, m, e);
            foreach (var stage in new[] { m.Before, m.AfterUp, m.AfterDown, m.AfterReUp })
                report.Evidence.Add(
                    $"{p}/Master L-10 {stage.Name}: history=[{string.Join(",", stage.History)}] " +
                    $"fingerprints=[{string.Join(",", stage.Fingerprints.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}:{f.Value[..Math.Min(12, f.Value.Length)]}"))}] " +
                    $"companies-rows={stage.CompaniesRows} tenants-with-company={stage.TenantsWithCompany}");
            foreach (var probe in m.Probes)
                report.Evidence.Add($"{p}/Master L-10 probe {probe.Name}: rejected={probe.Rejected} rolled-back={probe.RolledBack} — {probe.Detail}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An interrupted CREATE has already recorded its ownership outcome at the CREATE site.
            var reason = $"not completed: failed at stage '{progress.Stage}': {Describe(ex)}";
            result = IsInfrastructureFailure(ex) ? Evaluator.Infrastructure(Checks.L10, p, k, reason) : Evaluator.Failed(Checks.L10, p, k, reason);
        }
        finally
        {
            if (created)
            {
                var cleaned = await DropOwnedAsync(p, lifecycle, database, report);
                if (!cleaned && result is { Classification: Classification.Pass })
                    result = result with
                    {
                        Classification = Classification.FailInfrastructure,
                        Detail = "upgrade assertions held, but the database this run created could not be removed and verified absent; " + result.Detail,
                    };
            }

            if (result is not null) report.Add(result);
        }
    }

    /// <summary>How long the post-interruption existence check may take before it counts as inconclusive.</summary>
    internal static TimeSpan OwnershipCheckTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// F1 — a CREATE that failed or was CANCELLED may or may not have completed on the server, so
    /// the outcome is never assumed. The approved name is re-asserted and its existence checked on
    /// a separate, bounded token that the run's cancellation does not affect:
    ///   * absent            -> evidence only (nothing to clean up; nothing described as owned);
    ///   * present           -> ownership UNVERIFIED: NOT dropped, cleanup failure (exit 5);
    ///   * check failed, timed out or otherwise inconclusive -> UNVERIFIED, exit 5.
    /// Nothing is ever dropped here. Used by both the L-10 cycle and the from-empty cycle.
    /// </summary>
    internal static async Task RecordOwnershipAfterInterruptedCreateAsync(
        Provider p, string database, Func<CancellationToken, Task<bool>> exists, Report report, string context, Exception interruption)
    {
        var how = interruption is OperationCanceledException ? "was cancelled" : $"failed ({Describe(interruption)})";
        try
        {
            Safety.AssertApprovedTarget(p, database);
            using var bounded = new CancellationTokenSource(OwnershipCheckTimeout);
            if (await exists(bounded.Token))
                report.CleanupFailures.Add(
                    $"{p}/{context}: CREATE of '{database}' {how}, yet the database exists. Ownership is UNVERIFIED, so it was NOT dropped. " +
                    "Inspect it manually; removing it requires separate owner approval.");
            else
                report.Evidence.Add($"{p}/{context}: CREATE of '{database}' {how}; the database is confirmed absent — nothing was created, nothing to clean up");
        }
        catch (Exception ex)
        {
            report.CleanupFailures.Add(
                $"{p}/{context}: CREATE of '{database}' {how}, and whether the database exists could not be determined ({Describe(ex)}). " +
                "Ownership UNVERIFIED; nothing was dropped. Inspect manually before any further run.");
        }
    }

    private static async Task<bool> DropOwnedAsync(Provider p, IDatabaseLifecycle lifecycle, string database, Report report)
    {
        try
        {
            Safety.AssertApprovedTarget(p, database);
            await lifecycle.DropAsync(database, CancellationToken.None);
            if (await lifecycle.ExistsAsync(database, CancellationToken.None))
                throw new InvalidOperationException("database still listed after DROP");
            report.Evidence.Add($"{p}/Master L-10: dropped '{database}' (created by this run); absence verified");
            return true;
        }
        catch (Exception ex)
        {
            report.CleanupFailures.Add(
                $"{p}: L-10 database '{database}' (created by this run) could not be removed and verified absent: {Describe(ex)}. " +
                "Removing it manually requires separate owner approval.");
            return false;
        }
    }

    /// <summary>The live lifecycle: approved names only, on the endpoint's maintenance database.</summary>
    private sealed class ServerLifecycle(Provider p, string admin) : IDatabaseLifecycle
    {
        public Task<bool> ExistsAsync(string database, CancellationToken ct)
        {
            Safety.AssertApprovedTarget(p, database);
            return DatabaseExistsAsync(p, admin, database, ct);
        }

        public Task CreateAsync(string database, CancellationToken ct)
        {
            Safety.AssertApprovedTarget(p, database);
            return ExecuteAsync(p, admin, p == Provider.SqlServer ? $"CREATE DATABASE [{database}]" : $"CREATE DATABASE \"{database}\"", ct);
        }

        public Task DropAsync(string database, CancellationToken ct)
        {
            Safety.AssertApprovedTarget(p, database);
            if (p == Provider.SqlServer) SqlConnection.ClearAllPools(); else NpgsqlConnection.ClearAllPools();
            return ExecuteAsync(p, admin, p == Provider.SqlServer
                ? $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];"
                : $"DROP DATABASE \"{database}\" WITH (FORCE)", ct);
        }
    }

    // ── Measurement ──────────────────────────────────────────────────────────

    private static async Task<UpgradeMeasurement> MeasureUpgradeAsync(Scope s, ContextExpectations e, UpgradeProgress progress, CancellationToken ct)
    {
        var u = e.Upgrade ?? throw new InvalidOperationException("no upgrade expectation pinned for Master");
        var p = s.Provider;

        progress.Stage = $"migrate to Phase 1 head {u.From(p)}";
        await MigrateToAsync(s, u.From(p), ct);

        progress.Stage = "insert synthetic fixtures";
        var rows = await InsertFixturesAsync(s, ct);

        // F2: rows actually persisted, per fixture table — not the INSERT statements executed.
        progress.Stage = "count persisted fixture rows";
        var persisted = new Dictionary<string, long>();
        foreach (var table in u.FixtureTables)
            persisted[table] = await ScalarAsync(s.Provider, s.ConnectionString, $"SELECT COUNT(*) FROM {s.Q(table)}", ct);

        progress.Stage = "read original fixture columns";
        var columns = new Dictionary<string, string[]>();
        foreach (var table in u.FixtureTables)
            columns[table] = await ColumnsOfAsync(s, table, ct);

        progress.Stage = "measure before upgrade";
        var before = await StageAsync(s, "before", u, columns, ct);

        progress.Stage = $"migrate up to {u.To(p)}";
        await MigrateToAsync(s, u.To(p), ct);
        progress.Stage = "measure after up";
        var up = await StageAsync(s, "after-up", u, columns, ct);

        progress.Stage = "constraint probes";
        var probes = await ProbesAsync(s, u, ct);

        progress.Stage = $"migrate down to {u.From(p)}";
        await MigrateToAsync(s, u.From(p), ct);
        progress.Stage = "measure after down";
        var down = await StageAsync(s, "after-down", u, columns, ct);

        progress.Stage = $"re-apply {u.To(p)}";
        await MigrateToAsync(s, u.To(p), ct);
        progress.Stage = "measure after re-up";
        var reUp = await StageAsync(s, "after-re-up", u, columns, ct);

        return new UpgradeMeasurement(before, up, down, reUp, probes, rows, persisted);
    }

    private static async Task MigrateToAsync(Scope s, string migration, CancellationToken ct)
    {
        await using var context = s.Master();
        await context.GetService<IMigrator>().MigrateAsync(migration, ct);
    }

    private static async Task<UpgradeStage> StageAsync(
        Scope s, string name, UpgradeExpectation u, IReadOnlyDictionary<string, string[]> columns, CancellationToken ct)
    {
        var shape = await ShapeAsync(s, u, ct);
        var fingerprints = new Dictionary<string, string>();
        foreach (var (table, cols) in columns)
            fingerprints[table] = await FingerprintTableAsync(s, table, cols, ct);

        var companiesRows = shape.CompaniesTable ? await ScalarAsync(s.Provider, s.ConnectionString, $"SELECT COUNT(*) FROM {s.Q("Companies")}", ct) : -1;
        var tenantsWithCompany = shape.TenantCompanyIdColumn ? await TenantsWithCompanyAsync(s, ct) : -1;

        return new UpgradeStage(name, await HistoryAsync(s, ct), await MeasureSchemaAsync(s, ct), shape, fingerprints, companiesRows, tenantsWithCompany);
    }

    private static Task<long> TenantsWithCompanyAsync(Scope s, CancellationToken ct) =>
        ScalarAsync(s.Provider, s.ConnectionString, $"SELECT COUNT(*) FROM {s.Q("Tenants")} WHERE {s.Q("CompanyId")} IS NOT NULL", ct);

    private static async Task<CompanyShape> ShapeAsync(Scope s, UpgradeExpectation u, CancellationToken ct)
    {
        var p = s.Provider;
        var cs = s.ConnectionString;
        var sql = p == Provider.SqlServer;

        // SQL Server: upper-case INFORMATION_SCHEMA identifiers (as elsewhere in this harness),
        // so a case-sensitive server collation cannot break the catalog lookup.
        var table = await ScalarAsync(p, cs, sql
            ? "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @t AND TABLE_TYPE = 'BASE TABLE'"
            : "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @t AND table_type = 'BASE TABLE'",
            ct, ("@t", "Companies")) > 0;

        var companyColumns = await QueryAsync(p, cs, sql
            ? "SELECT COLUMN_NAME, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t"
            : "SELECT column_name, is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t",
            ct, ("@t", "Companies"));
        var tenantColumn = await QueryAsync(p, cs, sql
            ? "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME = @c"
            : "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = @c",
            ct, ("@t", "Tenants"), ("@c", "CompanyId"));

        var codeIndex = await QueryAsync(p, cs, sql
            ? "SELECT CAST(i.is_unique AS int) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE t.name = @t AND i.name = @i"
            : "SELECT CASE WHEN x.indisunique THEN 1 ELSE 0 END FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid " +
              "JOIN pg_class t ON t.oid = x.indrelid JOIN pg_namespace n ON n.oid = i.relnamespace " +
              "WHERE n.nspname = 'public' AND t.relname = @t AND i.relname = @i", ct, ("@t", "Companies"), ("@i", u.CodeIndex));
        var tenantIndex = await ScalarAsync(p, cs, sql
            ? "SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE t.name = @t AND i.name = @i"
            : "SELECT COUNT(*) FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid JOIN pg_class t ON t.oid = x.indrelid " +
              "JOIN pg_namespace n ON n.oid = i.relnamespace WHERE n.nspname = 'public' AND t.relname = @t AND i.relname = @i",
            ct, ("@t", "Tenants"), ("@i", u.TenantCompanyIdIndex)) > 0;
        var foreignKey = await QueryAsync(p, cs, sql
            ? "SELECT delete_referential_action_desc FROM sys.foreign_keys WHERE name = @f"
            : "SELECT confdeltype::text FROM pg_constraint WHERE conname = @f AND connamespace = 'public'::regnamespace", ct, ("@f", u.ForeignKey));

        string? deleteAction = foreignKey.Count == 0 ? null : (string?)foreignKey[0][0];
        if (!sql && deleteAction is not null)
            deleteAction = deleteAction switch { "r" => "RESTRICT", "a" => "NO_ACTION", "c" => "CASCADE", "n" => "SET_NULL", "d" => "SET_DEFAULT", var x => x };

        return new CompanyShape(
            table,
            companyColumns.Select(r => (string)r[0]!).ToArray(),
            companyColumns.Where(r => (string)r[1]! == "YES").Select(r => (string)r[0]!).ToArray(),
            tenantColumn.Count == 1,
            tenantColumn.Count == 1 && (string)tenantColumn[0][0]! == "YES",
            codeIndex.Count == 0 ? null : Convert.ToInt32(codeIndex[0][0], CultureInfo.InvariantCulture) == 1,
            tenantIndex,
            deleteAction);
    }

    // ── Synthetic fixtures (Phase 1 schema; no real data, no real credentials) ─

    private static readonly DateTime FixtureTime = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantActive = new("1a10a000-0000-4000-8000-000000000001");
    private static readonly Guid TenantInactive = new("1a10a000-0000-4000-8000-000000000002");
    private static readonly Guid UserOne = new("1a10a000-0000-4000-8000-000000000011");
    private static readonly Guid UserTwo = new("1a10a000-0000-4000-8000-000000000012");
    private static readonly Guid RoleTenant = new("1a10a000-0000-4000-8000-000000000021");
    private static readonly Guid RoleSystem = new("1a10a000-0000-4000-8000-000000000022");

    /// <summary>
    /// 2 Tenants (one inactive), 2 Users, 3 UserTenants, 2 Roles (one tenant role, one system
    /// role with NULL TenantId), 2 UserRoles. Whole-second UTC timestamps, fixed ids, canonical
    /// lower-case tenant codes, placeholder hashes. Returns the number of rows inserted.
    /// </summary>
    private static async Task<int> InsertFixturesAsync(Scope s, CancellationToken ct)
    {
        string Cols(params string[] names) => string.Join(", ", names.Select(s.Q));
        var rows = 0;

        async Task Insert(string table, string[] names, params (string Name, object Value)[] values)
        {
            var sql = $"INSERT INTO {s.Q(table)} ({Cols(names)}) VALUES ({string.Join(", ", values.Select(v => v.Name))})";
            await ExecuteWithAsync(s.Provider, s.ConnectionString, sql, ct, values);
            rows++;
        }

        string[] tenant = ["Id", "Code", "Name", "ConnectionString", "DbProvider", "IsActive", "CreatedAt"];
        await Insert("Tenants", tenant, ("@id", TenantActive), ("@code", "l10-active"), ("@name", "L10 Active Tenant"),
            ("@cs", "l10-placeholder-not-a-connection-string"), ("@prov", "SqlServer"), ("@active", true), ("@created", FixtureTime));
        await Insert("Tenants", tenant, ("@id", TenantInactive), ("@code", "l10-inactive"), ("@name", "L10 Inactive Tenant"),
            ("@cs", "l10-placeholder-not-a-connection-string"), ("@prov", "PostgreSql"), ("@active", false), ("@created", FixtureTime.AddDays(1)));

        string[] user =
        [
            "Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "PasswordHash", "SecurityStamp", "ConcurrencyStamp",
            "FirstName", "LastName", "IsLocked", "AccessFailedCount", "MustChangePassword", "IsActive", "CreatedAt", "IsDeleted",
        ];
        foreach (var (id, tag) in new[] { (UserOne, "one"), (UserTwo, "two") })
            await Insert("Users", user, ("@id", id), ("@un", $"l10-{tag}"), ("@nun", $"L10-{tag.ToUpperInvariant()}"),
                ("@em", $"l10-{tag}@example.invalid"), ("@nem", $"L10-{tag.ToUpperInvariant()}@EXAMPLE.INVALID"),
                ("@ph", "l10-placeholder-not-a-hash"), ("@ss", $"l10-stamp-{tag}"), ("@cc", $"l10-concurrency-{tag}"),
                ("@fn", "L10"), ("@ln", tag), ("@locked", false), ("@afc", 0), ("@mcp", false), ("@active", true),
                ("@created", FixtureTime), ("@deleted", false));

        string[] membership = ["Id", "UserId", "TenantId", "IsDefault", "HasGlobalPlantAccess", "IsActive", "JoinedAt"];
        await Insert("UserTenants", membership, ("@id", new Guid("1a10a000-0000-4000-8000-000000000031")), ("@u", UserOne), ("@t", TenantActive),
            ("@def", true), ("@gpa", false), ("@active", true), ("@joined", FixtureTime));
        await Insert("UserTenants", membership, ("@id", new Guid("1a10a000-0000-4000-8000-000000000032")), ("@u", UserOne), ("@t", TenantInactive),
            ("@def", false), ("@gpa", true), ("@active", true), ("@joined", FixtureTime.AddDays(2)));
        await Insert("UserTenants", membership, ("@id", new Guid("1a10a000-0000-4000-8000-000000000033")), ("@u", UserTwo), ("@t", TenantActive),
            ("@def", true), ("@gpa", false), ("@active", false), ("@joined", FixtureTime.AddDays(3)));

        // The system role's NULL TenantId is written as a literal so no untyped NULL parameter is needed.
        await ExecuteWithAsync(s.Provider, s.ConnectionString,
            $"INSERT INTO {s.Q("Roles")} ({Cols("Id", "Name", "NormalizedName", "TenantId", "IsSystemRole", "IsActive", "CreatedAt", "IsDeleted")}) " +
            "VALUES (@id, @n, @nn, @t, @sys, @active, @created, @deleted)", ct,
            ("@id", RoleTenant), ("@n", "L10 Tenant Role"), ("@nn", "L10 TENANT ROLE"), ("@t", TenantActive),
            ("@sys", false), ("@active", true), ("@created", FixtureTime), ("@deleted", false));
        rows++;
        await ExecuteWithAsync(s.Provider, s.ConnectionString,
            $"INSERT INTO {s.Q("Roles")} ({Cols("Id", "Name", "NormalizedName", "TenantId", "IsSystemRole", "IsActive", "CreatedAt", "IsDeleted")}) " +
            "VALUES (@id, @n, @nn, NULL, @sys, @active, @created, @deleted)", ct,
            ("@id", RoleSystem), ("@n", "L10 System Role"), ("@nn", "L10 SYSTEM ROLE"),
            ("@sys", true), ("@active", true), ("@created", FixtureTime), ("@deleted", false));
        rows++;

        string[] assignment = ["Id", "UserId", "RoleId", "TenantId", "AssignedAt", "IsActive"];
        await Insert("UserRoles", assignment, ("@id", new Guid("1a10a000-0000-4000-8000-000000000041")), ("@u", UserOne), ("@r", RoleTenant),
            ("@t", TenantActive), ("@at", FixtureTime), ("@active", true));
        await Insert("UserRoles", assignment, ("@id", new Guid("1a10a000-0000-4000-8000-000000000042")), ("@u", UserTwo), ("@r", RoleSystem),
            ("@t", TenantActive), ("@at", FixtureTime.AddDays(4)), ("@active", false));

        return rows;
    }

    // ── Fingerprints ─────────────────────────────────────────────────────────

    private static async Task<string[]> ColumnsOfAsync(Scope s, string table, CancellationToken ct)
    {
        var rows = await QueryAsync(s.Provider, s.ConnectionString, s.Provider == Provider.SqlServer
            ? "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t ORDER BY ORDINAL_POSITION"
            : "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t ORDER BY ordinal_position",
            ct, ("@t", table));
        var columns = rows.Select(r => (string)r[0]!).ToArray();
        return columns.Length > 0 && columns.Contains("Id")
            ? columns
            : throw new InvalidOperationException($"fixture table {table} has no readable columns");
    }

    /// <summary>
    /// SHA-256 over the given (original) columns of every row, ordered by Id, in a
    /// provider-neutral text form: lower-case GUIDs, 0/1 booleans, invariant numbers,
    /// length-prefixed strings, and UTC timestamps truncated to whole microseconds (so the
    /// approved SQL Server 100 ns / PostgreSQL 1 µs precision difference cannot register).
    /// </summary>
    private static async Task<string> FingerprintTableAsync(Scope s, string table, string[] columns, CancellationToken ct)
    {
        var rows = await QueryAsync(s.Provider, s.ConnectionString,
            $"SELECT {string.Join(", ", columns.Select(s.Q))} FROM {s.Q(table)} ORDER BY {s.Q("Id")}", ct);
        var text = string.Join("\n", rows.Select(r => string.Join("|", r.Select(Canonical))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{table}:{string.Join(",", columns)}\n{text}"))).ToLowerInvariant();
    }

    internal static string Canonical(object? value) => value switch
    {
        null or DBNull => "∅",
        Guid g => "g:" + g.ToString("D"),
        bool b => b ? "b:1" : "b:0",
        DateTime d => "t:" + Micro(d),
        DateTimeOffset o => "t:" + Micro(o.UtcDateTime),
        string text => $"s{text.Length}:{text}",
        byte or short or int or long => "n:" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string Micro(DateTime d) =>
        new DateTime(d.Ticks - d.Ticks % 10, DateTimeKind.Unspecified).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    // ── Constraint probes (rolled back; confirmed from a separate connection) ─

    private static async Task<IReadOnlyList<ProbeResult>> ProbesAsync(Scope s, UpgradeExpectation u, CancellationToken ct)
    {
        var p = s.Provider;
        var companies = s.Q("Companies");
        var tenants = s.Q("Tenants");
        var insertCompany = $"INSERT INTO {companies} ({s.Q("Id")}, {s.Q("Code")}, {s.Q("Name")}, {s.Q("IsActive")}, {s.Q("CreatedAt")}) " +
                            "VALUES (@id, @code, @name, @active, @created)";
        var probeCompany = new Guid("1a10a000-0000-4000-8000-000000000051");
        var unknownCompany = new Guid("1a10a000-0000-4000-8000-0000000000ff");

        (string, object)[] Company(Guid id, string code) =>
            [("@id", id), ("@code", code), ("@name", "L10 Probe Company"), ("@active", true), ("@created", FixtureTime)];

        return
        [
            await ProbeAsync(s, u.Probes[0], ct,
                arrange: (_, _) => Task.CompletedTask,
                violate: (c, tx) => ExecuteInAsync(c, tx, $"UPDATE {tenants} SET {s.Q("CompanyId")} = @c WHERE {s.Q("Id")} = @t", ct,
                    ("@c", unknownCompany), ("@t", TenantActive)),
                expected: IsForeignKeyViolation),
            await ProbeAsync(s, u.Probes[1], ct,
                arrange: async (c, tx) =>
                {
                    await ExecuteInAsync(c, tx, insertCompany, ct, Company(probeCompany, "l10-probe-restrict"));
                    await ExecuteInAsync(c, tx, $"UPDATE {tenants} SET {s.Q("CompanyId")} = @c WHERE {s.Q("Id")} = @t", ct,
                        ("@c", probeCompany), ("@t", TenantActive));
                },
                violate: (c, tx) => ExecuteInAsync(c, tx, $"DELETE FROM {companies} WHERE {s.Q("Id")} = @c", ct, ("@c", probeCompany)),
                expected: IsForeignKeyViolation),
            await ProbeAsync(s, u.Probes[2], ct,
                arrange: (c, tx) => ExecuteInAsync(c, tx, insertCompany, ct, Company(probeCompany, "l10-probe-duplicate")),
                violate: (c, tx) => ExecuteInAsync(c, tx, insertCompany, ct, Company(new Guid("1a10a000-0000-4000-8000-000000000052"), "l10-probe-duplicate")),
                expected: IsUniqueViolation),
        ];
    }

    private static async Task<ProbeResult> ProbeAsync(
        Scope s, string name, CancellationToken ct,
        Func<DbConnection, DbTransaction, Task> arrange,
        Func<DbConnection, DbTransaction, Task> violate,
        Func<Exception, bool> expected)
    {
        var rejected = false;
        string detail;
        await using (var connection = Open(s.Provider, s.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await arrange(connection, transaction);
                try
                {
                    await violate(connection, transaction);
                    detail = "the violating statement was ACCEPTED";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    rejected = expected(ex);
                    detail = (rejected ? "rejected as expected: " : "failed for an unexpected reason: ") + Describe(ex);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                detail = "probe setup failed: " + Describe(ex);
            }
            finally
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception ex) { detail = $"rollback threw {Describe(ex)}"; rejected = false; }
            }
        }

        // Fail closed: the rollback counts only if a separate connection sees no probe residue.
        bool rolledBack;
        try
        {
            rolledBack = await ScalarAsync(s.Provider, s.ConnectionString, $"SELECT COUNT(*) FROM {s.Q("Companies")}", ct) == 0
                         && await TenantsWithCompanyAsync(s, ct) == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rolledBack = false;
            detail += $"; rollback could not be confirmed: {Describe(ex)}";
        }

        return new ProbeResult(name, rejected, rolledBack, detail);
    }

    private static async Task ExecuteInAsync(DbConnection connection, DbTransaction transaction, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteWithAsync(Provider p, string cs, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = Open(p, cs);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameters(DbCommand command, (string Name, object Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
