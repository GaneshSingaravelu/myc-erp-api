using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using ERP.Application.Abstractions.Identity;
using ERP.DbMigrator;
using ERP.Identity.Entities;
using ERP.Identity.Persistence;
using ERP.Identity.Seed;
using ERP.Persistence.Context;
using ERP.Persistence.Interceptors;
using ERP.Production.Domain.Entities;
using ERP.Workflow.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ERP.ParityCheck;

public sealed record RunOptions(bool Live, IReadOnlySet<Provider> Providers, IReadOnlySet<DbContextKind> Contexts);

/// <summary>
/// LIVE RUNNER. For each provider and context: guard the target name, prove it does not exist,
/// create it, migrate it from empty, measure, classify, and ALWAYS drop it again — but only a
/// database this run created. Every live check is reported; nothing is silently omitted.
/// </summary>
public static class LiveRunner
{
    private static readonly Provider[] AllProviders = [Provider.SqlServer, Provider.PostgreSql];
    private static readonly DbContextKind[] AllContexts = [DbContextKind.Master, DbContextKind.AppDb];

    /// <summary>
    /// Fills <paramref name="report"/> (owned by the caller, so a cancelled run can still be
    /// rendered with any cleanup failures it recorded).
    /// </summary>
    public static async Task RunAsync(
        Report report, RunOptions options, IReadOnlyDictionary<string, string?> environment, Expectations expectations, CancellationToken ct)
    {
        var seeds = new Dictionary<Provider, SeedMeasurement?>();

        foreach (var provider in AllProviders)
        {
            var endpoint = Endpoint.Resolve(provider, environment);
            report.Evidence.Add(endpoint.Describe());

            var skip = !options.Providers.Contains(provider) ? "provider not selected for this run"
                : !options.Live ? "--live not supplied: no live database operation authorized"
                : !endpoint.Configured ? $"{Endpoint.VariableFor(provider)} not set: no endpoint configured"
                : null;
            if (skip is not null)
            {
                MarkAll(report, provider, AllContexts, (id, k) => Evaluator.Skipped(id, provider, k, skip));
                continue;
            }

            try
            {
                endpoint.Validate();
            }
            catch (SafetyRefusalException refusal)
            {
                report.SafetyRefusals.Add(refusal.Message);
                MarkAll(report, provider, AllContexts, (id, k) => Evaluator.Failed(id, provider, k, "not run: safety refusal"));
                continue;
            }

            report.Evidence.Add(endpoint.ContainerState());

            // A configured endpoint that cannot be reached is an INFRASTRUCTURE failure (D-k9):
            // still a failure, never a skip, and never counted as a parity defect.
            try
            {
                await ScalarAsync(provider, endpoint.AdminConnectionString(), "SELECT 1", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reason = $"configured endpoint unreachable: {Describe(ex)}";
                MarkAll(report, provider, AllContexts, (id, k) => Evaluator.Infrastructure(id, provider, k, reason));
                continue;
            }

            foreach (var kind in AllContexts)
            {
                if (!options.Contexts.Contains(kind))
                {
                    MarkAll(report, provider, [kind], (id, k) => Evaluator.Skipped(id, provider, k, "context not selected for this run"));
                    continue;
                }

                try
                {
                    var seed = await RunContextAsync(provider, kind, endpoint, expectations.For(kind), report, ct);
                    if (kind == DbContextKind.Master) seeds[provider] = seed;
                }
                catch (SafetyRefusalException refusal)
                {
                    report.SafetyRefusals.Add(refusal.Message);
                    MarkAll(report, provider, [kind], (id, k) => Evaluator.Failed(id, provider, k, "not run: safety refusal"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Setup failed (CREATE DATABASE, migration): every check not yet reported
                    // for this scope is reported as failed — classified, never dropped.
                    MarkRemaining(report, provider, kind, $"not run: context setup failed: {Describe(ex)}", IsInfrastructureFailure(ex));
                }
            }
        }

        report.Add(Evaluator.SeedEquivalence(seeds.GetValueOrDefault(Provider.SqlServer), seeds.GetValueOrDefault(Provider.PostgreSql)));
        report.Add(Evaluator.Deferred());
    }

    private static void MarkAll(Report report, Provider p, IEnumerable<DbContextKind> kinds, Func<string, DbContextKind, CheckResult> make)
    {
        foreach (var kind in kinds)
            foreach (var (id, k) in Checks.For(kind))
                report.Add(make(id, k));
    }

    private static void MarkRemaining(Report report, Provider p, DbContextKind kind, string reason, bool infrastructure)
    {
        var reported = report.Results.Where(r => r.Scope == Checks.Scope(p, kind)).Select(r => r.Id).ToHashSet();
        foreach (var (id, k) in Checks.For(kind).Where(c => !reported.Contains(c.Id)))
            report.Add(infrastructure ? Evaluator.Infrastructure(id, p, k, reason) : Evaluator.Failed(id, p, k, reason));
    }

    /// <summary>
    /// D-k9: is this exception the ENVIRONMENT failing (connectivity, authentication,
    /// permissions, resources, server shutdown) rather than a provider behaving differently?
    /// Constraint violations and assertion outcomes are NOT infrastructure.
    /// </summary>
    public static bool IsInfrastructureFailure(Exception ex) => InChain(ex, e => e switch
    {
        System.Net.Sockets.SocketException or TimeoutException or System.IO.IOException => true,
        // SQL State classes: 08 connection, 28 authorization, 53 insufficient resources,
        // 57P operator intervention; 3D000 invalid catalog; 42501 insufficient privilege.
        PostgresException pg => pg.SqlState.StartsWith("08", StringComparison.Ordinal)
                                || pg.SqlState.StartsWith("28", StringComparison.Ordinal)
                                || pg.SqlState.StartsWith("53", StringComparison.Ordinal)
                                || pg.SqlState.StartsWith("57P", StringComparison.Ordinal)
                                || pg.SqlState is "3D000" or "42501",
        // Any other NpgsqlException is client-side: connection, protocol, timeout.
        NpgsqlException => true,
        // -2 timeout; 2/53/40/64/233/10054/10060 network; 4060 cannot open database;
        // 18456 login failed; 262 permission denied (e.g. CREATE DATABASE).
        SqlException sql => sql.Number is -2 or 2 or 53 or 40 or 64 or 233 or 10054 or 10060 or 4060 or 18456 or 262,
        _ => false,
    });

    // ── One provider/context: create → migrate → measure → Down/Up → drop ────

    private static async Task<SeedMeasurement?> RunContextAsync(
        Provider p, DbContextKind kind, Endpoint endpoint, ContextExpectations e, Report report, CancellationToken ct)
    {
        var database = Safety.ApprovedDatabase(p);
        Safety.AssertApprovedTarget(p, database);
        var admin = endpoint.AdminConnectionString();

        if (await DatabaseExistsAsync(p, admin, database, ct))
            throw new SafetyRefusalException(
                $"{p}: '{database}' already exists. Refusing to reuse or overwrite it; it was NOT created by this run and will NOT be dropped.");

        var created = false;
        SeedMeasurement? seed = null;
        try
        {
            await ExecuteAsync(p, admin, p == Provider.SqlServer ? $"CREATE DATABASE [{database}]" : $"CREATE DATABASE \"{database}\"", ct);
            created = true;
            report.Evidence.Add($"{p}/{kind}: created disposable database '{database}'");

            var target = endpoint.TargetConnectionString();
            var scope = new Scope(p, kind, target);

            await using (var migrate = scope.Context())
                await migrate.Database.MigrateAsync(ct);

            await RunCheck(report, scope, Checks.L1, async () => Evaluator.Schema(p, kind, await MeasureSchemaAsync(scope, ct), e));
            await RunCheck(report, scope, Checks.L2, async () => Evaluator.History(p, kind, await HistoryAsync(scope, ct), e));

            if (kind == DbContextKind.Master)
            {
                await RunCheck(report, scope, Checks.L9, async () =>
                {
                    seed = await MeasureSeedAsync(scope, ct);
                    var result = Evaluator.Seed(p, kind, seed);
                    if (result.Classification != Classification.Pass) seed = null;
                    return result;
                });
                await RunCheck(report, scope, Checks.L3, async () => Evaluator.Precision(p, kind, await MeasurePrecisionAsync(scope, ct)));
                await RunCheck(report, scope, Checks.L5, async () => Evaluator.Utc(p, kind, await MeasureMasterUtcAsync(scope, ct)));
                await RunCheck(report, scope, Checks.L6, async () => Evaluator.NullableUnique(p, kind, await MeasureNullableUniqueAsync(scope, ct)));
                await RunCheck(report, scope, Checks.L7, async () => Evaluator.ForeignKeys(p, kind, await MeasureMasterForeignKeysAsync(scope, ct)));
                await RunCheck(report, scope, Checks.L8, async () => Evaluator.CodeCase(p, kind, await MeasureTenantCodeAsync(scope, ct)));
            }
            else
            {
                await RunCheck(report, scope, Checks.L4, async () => Evaluator.DateOnlyExtremes(p, kind, await MeasureDateOnlyAsync(scope, ct)));
                await RunCheck(report, scope, Checks.L5, async () => Evaluator.Utc(p, kind, await MeasureAppDbUtcAsync(scope, ct)));
                await RunCheck(report, scope, Checks.L7, async () => Evaluator.ForeignKeys(p, kind, await MeasureAppDbForeignKeysAsync(scope, ct)));
                await RunCheck(report, scope, Checks.L8, async () => Evaluator.CodeCase(p, kind, await MeasurePlantCodeAsync(scope, ct)));
            }

            await RunCheck(report, scope, Checks.L11, async () => Evaluator.DownUp(p, kind, await MeasureDownUpAsync(scope, ct), e));
            return seed;
        }
        finally
        {
            if (created)
                await DropAndVerifyAsync(p, admin, database, kind, report);
        }
    }

    private static async Task RunCheck(Report report, Scope s, string id, Func<Task<CheckResult>> check)
    {
        try
        {
            report.Add(await check());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = $"error: {Describe(ex)}";
            report.Add(IsInfrastructureFailure(ex)
                ? Evaluator.Infrastructure(id, s.Provider, s.Kind, reason)
                : Evaluator.Failed(id, s.Provider, s.Kind, reason));
        }
    }

    /// <summary>Cleanup runs on every exit path (normal, failure, cancellation). Never throws.</summary>
    private static async Task DropAndVerifyAsync(Provider p, string admin, string database, DbContextKind kind, Report report)
    {
        Safety.AssertApprovedTarget(p, database);
        var dropSql = p == Provider.SqlServer
            ? $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];"
            : $"DROP DATABASE \"{database}\" WITH (FORCE)";
        try
        {
            if (p == Provider.SqlServer) SqlConnection.ClearAllPools(); else NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(p, admin, dropSql, CancellationToken.None);
            if (await DatabaseExistsAsync(p, admin, database, CancellationToken.None))
                throw new InvalidOperationException("database still listed after DROP");
            report.Evidence.Add($"{p}/{kind}: dropped '{database}'; absence verified");
        }
        catch (Exception ex)
        {
            report.CleanupFailures.Add(
                $"{p}: '{database}' (created by this run) could not be removed: {Describe(ex)}. " +
                $"Recovery: connect to the {p} endpoint as an administrator and run: {dropSql} " +
                $"then confirm it is absent from {(p == Provider.SqlServer ? "sys.databases" : "pg_database")}.");
        }
    }

    // ── Context and raw-SQL plumbing ─────────────────────────────────────────

    // internal (not private) so the offline self-test can inspect how contexts are composed.
    internal sealed record Scope(Provider Provider, DbContextKind Kind, string ConnectionString)
    {
        private IConfiguration Configuration() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = Provider.ToString(),
                ["ConnectionStrings:MasterDb"] = ConnectionString,
                ["Tenants:0:ConnectionString"] = ConnectionString,
            })
            .Build();

        // The production design-time factories: same provider seams, migration assemblies and
        // configuration-assembly registration (Identity; Production + Workflow) as `dotnet ef`.
        public MasterDbContext Master() => new(MasterDbContextFactory.BuildOptions(Configuration()));

        public AppDbContext App() => new(AppDbContextFactory.BuildOptions(Configuration()));

        public DbContext Context() => Kind == DbContextKind.Master ? Master() : App();

        /// <summary>
        /// L-9 ONLY. The application's runtime composition attaches AuditableEntityInterceptor
        /// to MasterDbContext (InfrastructureServiceExtensions), and IdentitySeeder relies on it
        /// to stamp CreatedAt on AppRole/AppUser. The design-time factory attaches no
        /// interceptors, so the seeder context adds exactly this one — on top of the same
        /// provider options, migrations assembly and disposable connection string. Every other
        /// probe keeps the plain design-time context: those probes set timestamps themselves
        /// and must not have deletes turned into soft deletes.
        /// </summary>
        public DbContextOptions<MasterDbContext> SeederOptions() =>
            new DbContextOptionsBuilder<MasterDbContext>(MasterDbContextFactory.BuildOptions(Configuration()))
                .AddInterceptors(new AuditableEntityInterceptor(new ParitySystemUser()))
                .Options;

        public MasterDbContext SeederMaster() => new(SeederOptions());

        public string Q(string identifier) => Provider == Provider.SqlServer ? $"[{identifier}]" : $"\"{identifier}\"";
    }

    private static DbConnection Open(Provider p, string connectionString) =>
        p == Provider.SqlServer ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);

    private static async Task<List<object?[]>> QueryAsync(Provider p, string cs, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = Open(p, cs);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<long> ScalarAsync(Provider p, string cs, string sql, CancellationToken ct, params (string, object)[] parameters) =>
        Convert.ToInt64((await QueryAsync(p, cs, sql, ct, parameters)).Single()[0]);

    private static async Task ExecuteAsync(Provider p, string cs, string sql, CancellationToken ct)
    {
        await using var connection = Open(p, cs);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> DatabaseExistsAsync(Provider p, string admin, string database, CancellationToken ct) =>
        await ScalarAsync(p, admin,
            p == Provider.SqlServer ? "SELECT COUNT(*) FROM sys.databases WHERE name = @n" : "SELECT COUNT(*) FROM pg_database WHERE datname = @n",
            ct, ("@n", database)) > 0;

    private static string Describe(Exception ex)
    {
        var innermost = ex;
        while (innermost.InnerException is not null) innermost = innermost.InnerException;
        var message = innermost.Message.Split('\n')[0].Trim();
        return Redact.Secrets($"{ex.GetType().Name}{(innermost != ex ? " <- " + innermost.GetType().Name : "")}: {message}");
    }

    private static bool InChain(Exception ex, Func<Exception, bool> predicate)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (predicate(e)) return true;
        return false;
    }

    // The application-level UTC guard (UtcDateTime.ToStore), not a provider error.
    private static bool IsUtcGuardRejection(Exception ex) =>
        InChain(ex, e => e is InvalidOperationException && e.Message.Contains("Refusing to persist a DateTime", StringComparison.Ordinal));

    private static bool IsUniqueViolation(Exception ex) =>
        InChain(ex, e => e is SqlException { Number: 2601 or 2627 } || e is PostgresException { SqlState: "23505" });

    private static bool IsForeignKeyViolation(Exception ex) =>
        InChain(ex, e => e is SqlException { Number: 547 } || e is PostgresException { SqlState: "23503" });

    private static async Task<Exception?> TrySaveAsync(Func<DbContext> create, Action<DbContext> arrange, CancellationToken ct)
    {
        await using var context = create();
        arrange(context);
        try { await context.SaveChangesAsync(ct); return null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ex; }
    }

    private static async Task<Exception?> TryDeleteAsync<T>(Func<DbContext> create, Guid id, CancellationToken ct) where T : class
    {
        await using var context = create();
        try
        {
            await context.Set<T>().IgnoreQueryFilters().Where(x => EF.Property<Guid>(x, "Id") == id).ExecuteDeleteAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ex; }
    }

    private static DateTime Utc(long extraTicks = 0) => new DateTime(2026, 10, 10, 8, 30, 15, DateTimeKind.Utc).AddTicks(extraTicks);

    private static AppUser NewUser(string tag) => new()
    {
        UserName = $"p1k-{tag}", NormalizedUserName = $"P1K-{tag}".ToUpperInvariant(),
        Email = $"p1k-{tag}@example.invalid", NormalizedEmail = $"P1K-{tag}@EXAMPLE.INVALID".ToUpperInvariant(),
        PasswordHash = "parity-placeholder-not-a-hash", FirstName = "Parity", LastName = "Check", CreatedAt = Utc(),
    };

    private static Tenant NewTenant(string code, DateTime createdAt) => new()
    {
        Code = code, Name = "Parity " + code, ConnectionString = "parity-placeholder", DbProvider = "SqlServer", IsActive = true, CreatedAt = createdAt,
    };

    // ── L-1 / L-2 / L-11: schema, history, Down/Up ───────────────────────────

    private static string Categorize(Provider p, string dataType, long? maxLength) => (p, dataType) switch
    {
        (Provider.SqlServer, "uniqueidentifier") or (Provider.PostgreSql, "uuid") => Expectations.Guid,
        (Provider.SqlServer, "datetime2") or (Provider.PostgreSql, "timestamp with time zone") => Expectations.DateTime,
        (Provider.SqlServer, "bit") or (Provider.PostgreSql, "boolean") => Expectations.Bool,
        (Provider.SqlServer, "int") or (Provider.PostgreSql, "integer") => Expectations.Int,
        (Provider.SqlServer, "nvarchar") when maxLength == -1 => Expectations.UnboundedText,
        (Provider.PostgreSql, "text") => Expectations.UnboundedText,
        (Provider.SqlServer, "nvarchar") or (Provider.PostgreSql, "character varying") => Expectations.SizedText,
        (_, "date") => Expectations.Date,
        _ => $"unapproved:{dataType}",
    };

    private static string TablesSql(Provider p) => p == Provider.SqlServer
        ? "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME <> '__EFMigrationsHistory'"
        : "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory'";

    private static async Task<SchemaMeasurement> MeasureSchemaAsync(Scope s, CancellationToken ct)
    {
        var p = s.Provider;
        var tables = (await QueryAsync(p, s.ConnectionString, TablesSql(p), ct)).Select(r => (string)r[0]!).ToArray();

        var columns = await QueryAsync(p, s.ConnectionString, p == Provider.SqlServer
            ? "SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME <> '__EFMigrationsHistory'"
            : "SELECT data_type, character_maximum_length FROM information_schema.columns WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'", ct);
        var categories = columns
            .GroupBy(r => Categorize(p, (string)r[0]!, r[1] is null ? null : Convert.ToInt64(r[1])))
            .ToDictionary(g => g.Key, g => g.Count());

        string IndexSql(string extra) => p == Provider.SqlServer
            ? "SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id " +
              $"WHERE t.name <> '__EFMigrationsHistory' AND i.is_primary_key = 0 AND i.type > 0 {extra}"
            : "SELECT COUNT(*) FROM pg_index x JOIN pg_class c ON c.oid = x.indrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
              $"WHERE n.nspname = 'public' AND c.relname <> '__EFMigrationsHistory' AND NOT x.indisprimary {extra}";

        var indexes = await ScalarAsync(p, s.ConnectionString, IndexSql(""), ct);
        var unique = await ScalarAsync(p, s.ConnectionString, IndexSql(p == Provider.SqlServer ? "AND i.is_unique = 1" : "AND x.indisunique"), ct);
        var filtered = await ScalarAsync(p, s.ConnectionString, IndexSql(p == Provider.SqlServer ? "AND i.has_filter = 1" : "AND x.indpred IS NOT NULL"), ct);
        var foreignKeys = await ScalarAsync(p, s.ConnectionString, p == Provider.SqlServer
            ? "SELECT COUNT(*) FROM sys.foreign_keys"
            : "SELECT COUNT(*) FROM pg_constraint WHERE contype = 'f' AND connamespace = 'public'::regnamespace", ct);

        return new SchemaMeasurement(tables, columns.Count, categories, (int)indexes, (int)unique, (int)filtered, (int)foreignKeys);
    }

    private static async Task<string[]> HistoryAsync(Scope s, CancellationToken ct) =>
        (await QueryAsync(s.Provider, s.ConnectionString,
            $"SELECT {s.Q("MigrationId")} FROM {s.Q("__EFMigrationsHistory")} ORDER BY {s.Q("MigrationId")}", ct))
        .Select(r => (string)r[0]!).ToArray();

    private static async Task<DownUpMeasurement> MeasureDownUpAsync(Scope s, CancellationToken ct)
    {
        await using (var down = s.Context())
            await down.GetService<IMigrator>().MigrateAsync("0", ct);

        var tablesAfterDown = (await QueryAsync(s.Provider, s.ConnectionString, TablesSql(s.Provider), ct)).Count;
        var historyAfterDown = (int)await ScalarAsync(s.Provider, s.ConnectionString, $"SELECT COUNT(*) FROM {s.Q("__EFMigrationsHistory")}", ct);

        await using (var up = s.Context())
            await up.Database.MigrateAsync(ct);

        var tablesAfterUp = (await QueryAsync(s.Provider, s.ConnectionString, TablesSql(s.Provider), ct)).Count;
        return new DownUpMeasurement(tablesAfterDown, historyAfterDown, tablesAfterUp, await HistoryAsync(s, ct));
    }

    // ── L-3 precision (Master) ───────────────────────────────────────────────

    private static async Task<PrecisionMeasurement> MeasurePrecisionAsync(Scope s, CancellationToken ct)
    {
        var aligned = NewTenant("p1k-precision-aligned", Utc(1_234_560));
        var sub = NewTenant("p1k-precision-sub-us", Utc(1_234_567));
        if (await TrySaveAsync(s.Master, c => c.AddRange(aligned, sub), ct) is { } error) throw error;

        await using var read = s.Master();
        var a = await read.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == aligned.Id, ct);
        var b = await read.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == sub.Id, ct);
        return new PrecisionMeasurement(aligned.CreatedAt.Ticks, a.CreatedAt.Ticks, sub.CreatedAt.Ticks, b.CreatedAt.Ticks,
            a.CreatedAt.Kind == b.CreatedAt.Kind ? a.CreatedAt.Kind : DateTimeKind.Unspecified);
    }

    // ── L-4 DateOnly extremes (AppDb) ────────────────────────────────────────

    private static async Task<DateOnlyMeasurement> MeasureDateOnlyAsync(Scope s, CancellationToken ct)
    {
        var min = new WorkOrder("P1K-DATE-MIN", "Parity", 1, Guid.NewGuid(), Guid.NewGuid(), DateOnly.MinValue) { CreatedAt = Utc() };
        var max = new WorkOrder("P1K-DATE-MAX", "Parity", 1, Guid.NewGuid(), Guid.NewGuid(), DateOnly.MaxValue) { CreatedAt = Utc() };
        if (await TrySaveAsync(s.App, c => c.AddRange(min, max), ct) is { } error) throw error;

        async Task<string> Raw(Guid id) => (string)(await QueryAsync(s.Provider, s.ConnectionString, s.Provider == Provider.SqlServer
            ? "SELECT CONVERT(varchar(10), [DueDate], 23) FROM [WorkOrders] WHERE [Id] = @id"
            : "SELECT \"DueDate\"::text FROM \"WorkOrders\" WHERE \"Id\" = @id", ct, ("@id", id))).Single()[0]!;

        await using var read = s.App();
        var minRead = (await read.Set<WorkOrder>().AsNoTracking().SingleAsync(w => w.Id == min.Id, ct)).DueDate;
        var maxRead = (await read.Set<WorkOrder>().AsNoTracking().SingleAsync(w => w.Id == max.Id, ct)).DueDate;
        return new DateOnlyMeasurement(await Raw(min.Id), minRead, await Raw(max.Id), maxRead);
    }

    // ── L-5 UTC contract ─────────────────────────────────────────────────────

    private static async Task<UtcMeasurement> MeasureMasterUtcAsync(Scope s, CancellationToken ct)
    {
        var utc = NewTenant("p1k-utc-ok", Utc(6_543_210));
        if (await TrySaveAsync(s.Master, c => c.Add(utc), ct) is { } error) throw error;

        var localError = await TrySaveAsync(s.Master, c => c.Add(NewTenant("p1k-utc-local", DateTime.SpecifyKind(Utc(), DateTimeKind.Local))), ct);
        var unspecifiedError = await TrySaveAsync(s.Master, c => c.Add(NewTenant("p1k-utc-unspecified", DateTime.SpecifyKind(Utc(), DateTimeKind.Unspecified))), ct);

        var user = NewUser("utc-null");
        if (await TrySaveAsync(s.Master, c => c.Add(user), ct) is { } userError) throw userError;

        await using var read = s.Master();
        var back = await read.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == utc.Id, ct);
        var localPersisted = await read.Set<Tenant>().AnyAsync(t => t.Code == "p1k-utc-local", ct);
        var unspecifiedPersisted = await read.Set<Tenant>().AnyAsync(t => t.Code == "p1k-utc-unspecified", ct);
        var nullStored = await ScalarAsync(s.Provider, s.ConnectionString,
            $"SELECT COUNT(*) FROM {s.Q("Users")} WHERE {s.Q("Id")} = @id AND {s.Q("LastLoginAt")} IS NULL", ct, ("@id", user.Id)) == 1;
        var nullRead = (await read.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Id == user.Id, ct)).LastLoginAt is null;

        return new UtcMeasurement(
            back.CreatedAt.Ticks == utc.CreatedAt.Ticks, back.CreatedAt.Kind,
            localError is not null && IsUtcGuardRejection(localError), localPersisted,
            unspecifiedError is not null && IsUtcGuardRejection(unspecifiedError), unspecifiedPersisted,
            nullStored && nullRead, Notes(localError, unspecifiedError));
    }

    private static async Task<UtcMeasurement> MeasureAppDbUtcAsync(Scope s, CancellationToken ct)
    {
        var plant = new Plant { Code = "P1K-UTC-OK", Name = "Parity", CreatedAt = Utc(6_543_210) };
        if (await TrySaveAsync(s.App, c => c.Add(plant), ct) is { } error) throw error;

        var localError = await TrySaveAsync(s.App, c => c.Add(new Plant { Code = "P1K-UTC-LOCAL", Name = "x", CreatedAt = DateTime.SpecifyKind(Utc(), DateTimeKind.Local) }), ct);
        var unspecifiedError = await TrySaveAsync(s.App, c => c.Add(new Plant { Code = "P1K-UTC-UNSPECIFIED", Name = "x", CreatedAt = DateTime.SpecifyKind(Utc(), DateTimeKind.Unspecified) }), ct);

        var order = new WorkOrder("P1K-UTC-NULL", "Parity", 1, Guid.NewGuid(), plant.Id) { CreatedAt = Utc() };
        if (await TrySaveAsync(s.App, c => c.Add(order), ct) is { } orderError) throw orderError;

        await using var read = s.App();
        var back = await read.Set<Plant>().AsNoTracking().SingleAsync(x => x.Id == plant.Id, ct);
        var localPersisted = await read.Set<Plant>().AnyAsync(x => x.Code == "P1K-UTC-LOCAL", ct);
        var unspecifiedPersisted = await read.Set<Plant>().AnyAsync(x => x.Code == "P1K-UTC-UNSPECIFIED", ct);
        var nullStored = await ScalarAsync(s.Provider, s.ConnectionString,
            $"SELECT COUNT(*) FROM {s.Q("WorkOrders")} WHERE {s.Q("Id")} = @id AND {s.Q("ReleasedAt")} IS NULL", ct, ("@id", order.Id)) == 1;
        var nullRead = (await read.Set<WorkOrder>().AsNoTracking().SingleAsync(w => w.Id == order.Id, ct)).ReleasedAt is null;

        return new UtcMeasurement(
            back.CreatedAt.Ticks == plant.CreatedAt.Ticks, back.CreatedAt.Kind,
            localError is not null && IsUtcGuardRejection(localError), localPersisted,
            unspecifiedError is not null && IsUtcGuardRejection(unspecifiedError), unspecifiedPersisted,
            nullStored && nullRead, Notes(localError, unspecifiedError));
    }

    private static string Notes(params Exception?[] errors) =>
        string.Join("; ", errors.Where(e => e is not null && !IsUtcGuardRejection(e)).Select(e => Describe(e!)));

    // ── L-6 nullable unique index (Master) ───────────────────────────────────

    private static async Task<NullableUniqueMeasurement> MeasureNullableUniqueAsync(Scope s, CancellationToken ct)
    {
        var tenant = NewTenant("p1k-unique-tenant", Utc());
        if (await TrySaveAsync(s.Master, c => c.Add(tenant), ct) is { } error) throw error;

        AppRole Role(Guid? tenantId) => new() { Name = "P1K Role", NormalizedName = "P1K-ROLE", TenantId = tenantId, CreatedAt = Utc() };

        var firstNull = await TrySaveAsync(s.Master, c => c.Add(Role(null)), ct);
        var secondNull = await TrySaveAsync(s.Master, c => c.Add(Role(null)), ct);
        var firstTenant = await TrySaveAsync(s.Master, c => c.Add(Role(tenant.Id)), ct);
        var secondTenant = await TrySaveAsync(s.Master, c => c.Add(Role(tenant.Id)), ct);

        var notes = string.Join("; ", new[] { firstNull, secondNull, firstTenant }.Where(e => e is not null).Select(e => Describe(e!)));
        return new NullableUniqueMeasurement(
            firstNull is null && secondNull is null,
            firstTenant is null && secondTenant is not null && IsUniqueViolation(secondTenant),
            notes);
    }

    // ── L-7 foreign keys ─────────────────────────────────────────────────────

    private static async Task<ForeignKeyMeasurement> MeasureMasterForeignKeysAsync(Scope s, CancellationToken ct)
    {
        // Enforcement: a UserTenant pointing at nothing.
        var orphan = await TrySaveAsync(s.Master, c => c.Add(new UserTenant { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), JoinedAt = Utc() }), ct);

        // Cascade: FK_UserTenants_Tenants_TenantId.
        var cascadeTenant = NewTenant("p1k-fk-cascade", Utc());
        var user = NewUser("fk");
        if (await TrySaveAsync(s.Master, c =>
            {
                c.AddRange(cascadeTenant, user);
                c.Add(new UserTenant { UserId = user.Id, TenantId = cascadeTenant.Id, JoinedAt = Utc() });
            }, ct) is { } error) throw error;
        if (await TryDeleteAsync<Tenant>(s.Master, cascadeTenant.Id, ct) is { } deleteError) throw deleteError;

        // Restrict: FK_Roles_Tenants_TenantId.
        var restrictTenant = NewTenant("p1k-fk-restrict", Utc());
        if (await TrySaveAsync(s.Master, c =>
            {
                c.Add(restrictTenant);
                c.Add(new AppRole { Name = "P1K Restrict", NormalizedName = "P1K-RESTRICT", TenantId = restrictTenant.Id, CreatedAt = Utc() });
            }, ct) is { } restrictSetup) throw restrictSetup;
        var restrict = await TryDeleteAsync<Tenant>(s.Master, restrictTenant.Id, ct);

        await using var read = s.Master();
        return new ForeignKeyMeasurement(
            orphan is not null && IsForeignKeyViolation(orphan),
            !await read.Set<UserTenant>().AnyAsync(x => x.TenantId == cascadeTenant.Id, ct),
            restrict is not null && IsForeignKeyViolation(restrict),
            await read.Set<Tenant>().AnyAsync(t => t.Id == restrictTenant.Id, ct),
            string.Join("; ", new[] { orphan, restrict }.Where(e => e is not null && !IsForeignKeyViolation(e)).Select(e => Describe(e!))));
    }

    private static async Task<ForeignKeyMeasurement> MeasureAppDbForeignKeysAsync(Scope s, CancellationToken ct)
    {
        // Enforcement: FK_UserPlantAccess_Plants_PlantId with no plant.
        var orphan = await TrySaveAsync(s.App, c => c.Add(new UserPlantAccess { UserId = Guid.NewGuid(), PlantId = Guid.NewGuid(), GrantedAt = Utc() }), ct);

        // Cascade: deleting a plant removes its access rows.
        var plant = new Plant { Code = "P1K-FK-CASCADE", Name = "Parity", CreatedAt = Utc() };
        if (await TrySaveAsync(s.App, c =>
            {
                c.Add(plant);
                c.Add(new UserPlantAccess { UserId = Guid.NewGuid(), PlantId = plant.Id, GrantedAt = Utc() });
            }, ct) is { } error) throw error;
        if (await TryDeleteAsync<Plant>(s.App, plant.Id, ct) is { } deleteError) throw deleteError;

        // Restrict: FK_ProcessSteps_ProcessSteps_NextStepId.
        var template = new ProcessTemplate("P1K Restrict Template") { CreatedAt = Utc() };
        var first = template.AddStep("Cutting", 1);
        var second = template.AddStep("Packing", 2);
        first.CreatedAt = Utc();
        second.CreatedAt = Utc();
        if (await TrySaveAsync(s.App, c => c.Add(template), ct) is { } templateError) throw templateError;
        await using (var link = s.App())
        {
            var tracked = await link.Set<ProcessStep>().SingleAsync(x => x.Id == first.Id, ct);
            tracked.SetNextStep(second.Id);
            await link.SaveChangesAsync(ct);
        }
        var restrict = await TryDeleteAsync<ProcessStep>(s.App, second.Id, ct);

        await using var read = s.App();
        return new ForeignKeyMeasurement(
            orphan is not null && IsForeignKeyViolation(orphan),
            !await read.Set<UserPlantAccess>().AnyAsync(x => x.PlantId == plant.Id, ct),
            restrict is not null && IsForeignKeyViolation(restrict),
            await read.Set<ProcessStep>().IgnoreQueryFilters().AnyAsync(x => x.Id == second.Id, ct),
            string.Join("; ", new[] { orphan, restrict }.Where(e => e is not null && !IsForeignKeyViolation(e)).Select(e => Describe(e!))));
    }

    // ── L-8 code case-sensitivity ────────────────────────────────────────────

    private static async Task<CodeCaseMeasurement> MeasureTenantCodeAsync(Scope s, CancellationToken ct)
    {
        var canonical = TenantCode.Normalize("  P1K-Case-Tenant ");
        if (await TrySaveAsync(s.Master, c => c.Add(NewTenant(canonical, Utc())), ct) is { } error) throw error;

        bool lookup, raw;
        await using (var read = s.Master())
        {
            lookup = await new TenantRepository(read).GetByCodeAsync("P1K-CASE-TENANT", ct) is not null;
            raw = await read.Set<Tenant>().AnyAsync(t => t.Code == "P1K-CASE-TENANT", ct);
        }

        // Bypasses canonicalization on purpose: what does the ENGINE accept?
        var duplicate = await TrySaveAsync(s.Master, c => c.Add(NewTenant("P1K-CASE-TENANT", Utc())), ct);
        if (duplicate is not null && !IsUniqueViolation(duplicate)) throw duplicate;

        var (collation, literal) = await MeasureCollationAsync(s, "Tenants", ct);
        return new CodeCaseMeasurement("Tenant.Code", lookup, raw, duplicate is null, collation, literal);
    }

    /// <summary>
    /// D-k11 evidence: the collations actually in force for the disposable database and the
    /// Code column, plus how the database default compares two literals. Recorded, not used to
    /// classify — classification uses the comparison measured on the real column.
    /// </summary>
    private static async Task<(string Collation, bool? LiteralCaseInsensitive)> MeasureCollationAsync(Scope s, string table, CancellationToken ct)
    {
        try
        {
            if (s.Provider == Provider.SqlServer)
            {
                var row = (await QueryAsync(s.Provider, s.ConnectionString,
                    "SELECT CAST(SERVERPROPERTY('Collation') AS nvarchar(128)), " +
                    "CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)), " +
                    "(SELECT c.collation_name FROM sys.columns c WHERE c.object_id = OBJECT_ID(@t) AND c.name = 'Code'), " +
                    "CASE WHEN N'A' = N'a' THEN 1 ELSE 0 END", ct, ("@t", table))).Single();
                return ($"server={row[0]}, database={row[1]}, {table}.Code={row[2] ?? "database default"}", Convert.ToInt32(row[3]) == 1);
            }

            var pg = (await QueryAsync(s.Provider, s.ConnectionString,
                "SELECT d.datcollate, d.datctype, d.datlocprovider::text, " +
                "(SELECT collation_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = 'Code'), " +
                "CASE WHEN 'A' = 'a' THEN 1 ELSE 0 END FROM pg_database d WHERE d.datname = current_database()", ct, ("@t", table))).Single();
            return ($"datcollate={pg[0]}, datctype={pg[1]}, locale-provider={pg[2]}, {table}.Code={pg[3] ?? "database default"}", Convert.ToInt32(pg[4]) == 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ($"unavailable ({Describe(ex)})", null);
        }
    }

    private static async Task<CodeCaseMeasurement> MeasurePlantCodeAsync(Scope s, CancellationToken ct)
    {
        if (await TrySaveAsync(s.App, c => c.Add(new Plant { Code = "P1K-PLANT-CASE", Name = "Parity", CreatedAt = Utc() }), ct) is { } error) throw error;

        bool raw;
        await using (var read = s.App())
            raw = await read.Set<Plant>().AnyAsync(x => x.Code == "p1k-plant-case", ct);

        var duplicate = await TrySaveAsync(s.App, c => c.Add(new Plant { Code = "p1k-plant-case", Name = "Parity", CreatedAt = Utc() }), ct);
        if (duplicate is not null && !IsUniqueViolation(duplicate)) throw duplicate;

        var (collation, literal) = await MeasureCollationAsync(s, "Plants", ct);
        return new CodeCaseMeasurement("Plant.Code", null, raw, duplicate is null, collation, literal);
    }

    // ── L-9 IdentitySeeder ───────────────────────────────────────────────────

    /// <summary>
    /// Runs the real IdentitySeeder twice. The administrator secret is an EPHEMERAL random
    /// value: generated in memory for this disposable database only, never printed, never
    /// persisted by the harness, never compared. Password hashes, security stamps, ids and
    /// timestamps are excluded from the structure fingerprint.
    ///
    /// <para>IdentitySeeder catches and LOGS its own exceptions, so a swallowed failure would
    /// otherwise surface only as missing rows. The capturing logger records Error/Critical
    /// entries (redacted, first line only) and they fail L-9 outright.</para>
    /// </summary>
    private static async Task<SeedMeasurement> MeasureSeedAsync(Scope s, CancellationToken ct)
    {
        var ephemeralSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "aA1!";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SeedSettings:DefaultAdminPassword"] = ephemeralSecret,
                ["Tenants:0:Id"] = "5a7d9c1e-1f4b-4c2a-9e3d-0000000a1f9c",
                ["Tenants:0:Identifier"] = "parity-seed-tenant",
                ["Tenants:0:Name"] = "Parity Seed Tenant",
                ["Tenants:0:ConnectionString"] = "parity-placeholder",
                ["Tenants:0:DbProvider"] = "SqlServer",
                ["Tenants:0:IsActive"] = "true",
            })
            .Build();

        var seederLog = new CapturingLogger<MasterDbContext>();
        var services = new ServiceCollection();
        services.AddScoped(_ => s.SeederMaster());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new ParityHostEnvironment());
        services.AddSingleton<ILogger<MasterDbContext>>(seederLog);
        await using var provider = services.BuildServiceProvider();

        await IdentitySeeder.SeedAsync(provider);
        var (counts, fingerprint) = await FingerprintAsync(s, ct);

        await IdentitySeeder.SeedAsync(provider);
        var (countsAgain, fingerprintAgain) = await FingerprintAsync(s, ct);

        return new SeedMeasurement(counts, fingerprint,
            fingerprint == fingerprintAgain && counts.OrderBy(kv => kv.Key).SequenceEqual(countsAgain.OrderBy(kv => kv.Key)),
            seederLog.Errors.ToArray());
    }

    private static async Task<(Dictionary<string, int> Counts, string Fingerprint)> FingerprintAsync(Scope s, CancellationToken ct)
    {
        await using var c = s.Master();
        var tenants = await c.Set<Tenant>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var users = await c.Set<AppUser>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var roles = await c.Set<AppRole>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var permissions = await c.Set<Permission>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var modules = await c.Set<Module>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var menus = await c.Set<Menu>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var menuPermissions = await c.Set<MenuPermission>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var rolePermissions = await c.Set<AppRolePermission>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var userRoles = await c.Set<AppUserRole>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var userTenants = await c.Set<UserTenant>().IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);

        var tenantCode = tenants.ToDictionary(t => t.Id, t => t.Code);
        var userName = users.ToDictionary(u => u.Id, u => u.NormalizedUserName);
        var roleName = roles.ToDictionary(r => r.Id, r => r.NormalizedName);
        var permissionCode = permissions.ToDictionary(p => p.Id, p => p.Code);
        var moduleCode = modules.ToDictionary(m => m.Id, m => m.Code);
        var menuCode = menus.ToDictionary(m => m.Id, m => m.Code);
        string T(Guid? id) => id is { } g && tenantCode.TryGetValue(g, out var code) ? code : "-";

        // Non-secret, provider-independent identity only. Deliberately excluded:
        // PasswordHash, SecurityStamp, ConcurrencyStamp, every Id and every timestamp.
        var lines = new List<string>();
        lines.AddRange(tenants.Select(t => $"tenant|{t.Code}|{t.Name}|{t.IsActive}"));
        lines.AddRange(users.Select(u => $"user|{u.NormalizedUserName}|{u.NormalizedEmail}|{u.IsLocked}|{u.MustChangePassword}|{u.IsActive}"));
        lines.AddRange(roles.Select(r => $"role|{r.NormalizedName}|{r.IsSystemRole}|{T(r.TenantId)}"));
        lines.AddRange(permissions.Select(p => $"permission|{p.Code}|{p.Category}"));
        lines.AddRange(modules.Select(m => $"module|{m.Code}|{m.DisplayOrder}|{m.IsActive}"));
        lines.AddRange(menus.Select(m => $"menu|{m.Code}|{moduleCode.GetValueOrDefault(m.ModuleId)}|{(m.ParentMenuId is { } pm ? menuCode.GetValueOrDefault(pm) : "-")}|{m.DisplayOrder}|{m.IsVisible}|{m.IsEnabled}"));
        lines.AddRange(menuPermissions.Select(m => $"menu-permission|{menuCode.GetValueOrDefault(m.MenuId)}|{permissionCode.GetValueOrDefault(m.PermissionId)}|{m.IsRequired}"));
        lines.AddRange(rolePermissions.Select(r => $"role-permission|{roleName.GetValueOrDefault(r.RoleId)}|{permissionCode.GetValueOrDefault(r.PermissionId)}|{r.IsAllowed}"));
        lines.AddRange(userRoles.Select(u => $"user-role|{userName.GetValueOrDefault(u.UserId)}|{roleName.GetValueOrDefault(u.RoleId)}|{T(u.TenantId)}|{u.IsActive}"));
        lines.AddRange(userTenants.Select(u => $"user-tenant|{userName.GetValueOrDefault(u.UserId)}|{T(u.TenantId)}|{u.IsDefault}|{u.HasGlobalPlantAccess}|{u.IsActive}"));
        lines.Sort(StringComparer.Ordinal);

        var counts = new Dictionary<string, int>
        {
            ["Tenants"] = tenants.Count, ["Users"] = users.Count, ["Roles"] = roles.Count, ["Permissions"] = permissions.Count,
            ["Modules"] = modules.Count, ["Menus"] = menus.Count, ["MenuPermissions"] = menuPermissions.Count,
            ["RolePermissions"] = rolePermissions.Count, ["UserRoles"] = userRoles.Count, ["UserTenants"] = userTenants.Count,
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
        return (counts, fingerprint);
    }

    /// <summary>Minimal host environment for IdentitySeeder (Development: seeds sample users too).</summary>
    private sealed class ParityHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ERP.ParityCheck";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// The anonymous system actor for L-9 seeding: no user, no tenant, unauthenticated — so
/// AuditableEntityInterceptor stamps CreatedAt with CreatedBy = null, as at first-run startup.
/// </summary>
internal sealed class ParitySystemUser : ICurrentUserService
{
    public string? UserId => null;
    public string? Email => null;
    public string? TenantId => null;
    public bool IsAuthenticated => false;
    public bool IsInRole(string role) => false;
    public string? IpAddress => null;
    public string? UserAgent => null;
}

/// <summary>
/// Captures Error/Critical entries logged by IdentitySeeder (which swallows its own exceptions)
/// so L-9 can fail on them. Stores only "OuterType [&lt;- InnermostType]: first line of the
/// innermost message", redacted and bounded. Never calls Exception.ToString(), so no stack
/// trace is kept; nothing is written anywhere — the entries live in memory for one check.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private const int MaxLength = 300;
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors
    {
        get { lock (_errors) return _errors.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        string entry;
        if (exception is null)
        {
            entry = FirstLine(formatter(state, null));
        }
        else
        {
            var innermost = exception;
            while (innermost.InnerException is not null) innermost = innermost.InnerException;
            var types = innermost == exception ? exception.GetType().Name : $"{exception.GetType().Name} <- {innermost.GetType().Name}";
            entry = $"{types}: {FirstLine(innermost.Message)}";
        }

        entry = Redact.Secrets(entry);
        if (entry.Length > MaxLength) entry = entry[..MaxLength] + "…";
        lock (_errors) _errors.Add($"{logLevel}: {entry}");
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}
