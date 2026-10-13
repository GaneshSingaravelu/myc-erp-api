using ERP.ParityCheck;

// ERP.ParityCheck — live SQL Server / PostgreSQL provider-parity harness (P1-k / DB-03).
//
// Nothing touches a database unless BOTH are true:
//   1. --live is supplied (explicit authorization), and
//   2. the provider's endpoint variable holds a server-level connection string.
// The only databases ever created or dropped are the approved disposable names, and only
// when this run proved they did not already exist. See ExitCodes for the exit-code contract.

const string Usage = """
    ERP.ParityCheck — provider parity harness (P1-k / DB-03)

    Usage:
      ERP.ParityCheck [--live] [--provider all|sqlserver|postgresql] [--context all|master|appdb] [--json <file>]
      ERP.ParityCheck --simulate <no-endpoint|wrong-expected|one-provider|normal> [--json <file>]
      ERP.ParityCheck --self-test
      ERP.ParityCheck --help

    Live endpoints (server-level connection strings; never printed):
      ERP_PARITY_SQLSERVER             SQL Server endpoint
      ERP_PARITY_POSTGRESQL            PostgreSQL endpoint
      ERP_PARITY_SQLSERVER_CONTAINER   optional: container name for read-only `docker inspect` evidence
      ERP_PARITY_POSTGRESQL_CONTAINER  optional: container name for read-only `docker inspect` evidence

    Approved endpoint identity (REQUIRED for --live; non-secret; must match the endpoint exactly):
      ERP_PARITY_SQLSERVER_EXPECTED_DATASOURCE  loopback data source, e.g. localhost,14333 or localhost\SQLDEV
      ERP_PARITY_POSTGRESQL_EXPECTED_HOSTPORT   loopback host:port, e.g. localhost:5433
      A match proves only that the configuration points at that loopback endpoint — not that
      the server is disposable. Remote hosts, LocalDB, pipes, sockets and multi-host are refused.

    Disposable databases (the ONLY names ever created or dropped):
      SQL Server:  ERP_P1k_Parity_SqlServer_Throwaway
      PostgreSQL:  ERP_P1k_Parity_PostgreSql_Throwaway

    Exit codes: 0 success (full matrix, no fail, no skip) · 1 failure · 2 incomplete (skips)
                3 usage · 4 safety refusal · 5 cleanup failure        Precedence: 5 > 4 > 1 > 2 > 0
    """;

string? simulate = null, json = null;
bool live = false, selfTest = false;
var providers = new HashSet<Provider> { Provider.SqlServer, Provider.PostgreSql };
var contexts = new HashSet<DbContextKind> { DbContextKind.Master, DbContextKind.AppDb };

try
{
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} requires a value");
        switch (args[i])
        {
            case "--live": live = true; break;
            case "--self-test": selfTest = true; break;
            case "--simulate": simulate = Next(); break;
            case "--json": json = Next(); break;
            case "--provider":
                providers = Next() switch
                {
                    "all" => [Provider.SqlServer, Provider.PostgreSql],
                    "sqlserver" => [Provider.SqlServer],
                    "postgresql" => [Provider.PostgreSql],
                    var v => throw new ArgumentException($"unknown provider '{v}'"),
                };
                break;
            case "--context":
                contexts = Next() switch
                {
                    "all" => [DbContextKind.Master, DbContextKind.AppDb],
                    "master" => [DbContextKind.Master],
                    "appdb" => [DbContextKind.AppDb],
                    var v => throw new ArgumentException($"unknown context '{v}'"),
                };
                break;
            case "--help" or "-h":
                Console.WriteLine(Usage);
                return ExitCodes.Success;
            default:
                throw new ArgumentException($"unknown argument '{args[i]}'");
        }
    }

    if (new[] { live, selfTest, simulate is not null }.Count(x => x) > 1)
        throw new ArgumentException("--live, --simulate and --self-test are mutually exclusive");
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"usage error: {ex.Message}");
    Console.Error.WriteLine(Usage);
    return ExitCodes.Usage;
}

if (selfTest)
    return await SelfTest.RunAsync(Console.Out);

Report report;
if (simulate is not null)
{
    report = simulate switch
    {
        "normal" => Simulation.Run(Expectations.Approved, providers),
        "one-provider" => Simulation.Run(Expectations.Approved, new HashSet<Provider> { Provider.SqlServer }),
        "no-endpoint" => Simulation.Run(Expectations.Approved, new HashSet<Provider>()),
        "wrong-expected" => Simulation.Run(Expectations.Approved with
        {
            Master = Expectations.Approved.Master with { PostgreSqlLineage = [.. Expectations.Approved.Master.PostgreSqlLineage, "29990101000000_NotARealMigration"] },
        }, providers),
        _ => null!,
    };
    if (report is null)
    {
        Console.Error.WriteLine($"usage error: unknown simulation '{simulate}'");
        return ExitCodes.Usage;
    }
}
else
{
    // Only the harness's own variables are read — never the rest of the environment.
    string[] variables =
    [
        Endpoint.SqlServerVariable, Endpoint.PostgreSqlVariable,
        Endpoint.SqlServerContainerVariable, Endpoint.PostgreSqlContainerVariable,
        Endpoint.SqlServerExpectedVariable, Endpoint.PostgreSqlExpectedVariable,
    ];
    var environment = variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // let cleanup run
        cancellation.Cancel();
    };

    report = new Report(RunMode.Live);
    try
    {
        await LiveRunner.RunAsync(report, new RunOptions(live, providers, contexts), environment, Expectations.Approved, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        report.Add(new CheckResult("RUN", "run", "All", Classification.FailDefect,
            "cancelled by operator; cleanup was attempted for any database this run created — see cleanup lines"));
    }
}

Console.Write(report.Render());
if (json is not null)
    await File.WriteAllTextAsync(json, report.ToJson());
return report.ExitCode;
