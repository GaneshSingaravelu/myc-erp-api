using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace ERP.ParityCheck;

/// <summary>Thrown when a guard refuses to operate. Nothing is created after a refusal.</summary>
public sealed class SafetyRefusalException(string message) : Exception(message);

/// <summary>
/// DATABASE SAFETY. The harness may create, migrate and drop exactly one database name per
/// provider, and only one it created itself in the current run.
/// </summary>
public static class Safety
{
    public const string SqlServerDatabase = "ERP_P1k_Parity_SqlServer_Throwaway";
    public const string PostgreSqlDatabase = "ERP_P1k_Parity_PostgreSql_Throwaway";

    /// <summary>Names the harness must never target, regardless of configuration.</summary>
    public static readonly IReadOnlySet<string> ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ERP_Master", "ERP_Dev", "ERP_TestCo", "ERP_PG_Dev",
        "master", "model", "msdb", "tempdb", "postgres", "template0", "template1",
    };

    public static string ApprovedDatabase(Provider p) => p == Provider.SqlServer ? SqlServerDatabase : PostgreSqlDatabase;

    public static string MaintenanceDatabase(Provider p) => p == Provider.SqlServer ? "master" : "postgres";

    /// <summary>Exact, ordinal match to the approved name, and never a protected name.</summary>
    public static void AssertApprovedTarget(Provider p, string database)
    {
        if (!string.Equals(database, ApprovedDatabase(p), StringComparison.Ordinal))
            throw new SafetyRefusalException($"{p}: target '{database}' is not the approved disposable database '{ApprovedDatabase(p)}'.");
        if (ProtectedNames.Contains(database))
            throw new SafetyRefusalException($"{p}: target '{database}' is a protected database name.");
    }
}

/// <summary>
/// A provider endpoint, configured ONLY through an environment variable holding a
/// server-level connection string. Nothing is read from appsettings; no credential is ever
/// invented, printed or persisted. <see cref="Describe"/> exposes host and port only.
/// </summary>
public sealed class Endpoint
{
    public const string SqlServerVariable = "ERP_PARITY_SQLSERVER";
    public const string PostgreSqlVariable = "ERP_PARITY_POSTGRESQL";
    public const string SqlServerContainerVariable = "ERP_PARITY_SQLSERVER_CONTAINER";
    public const string PostgreSqlContainerVariable = "ERP_PARITY_POSTGRESQL_CONTAINER";

    private readonly string? _raw;

    private Endpoint(Provider provider, string? raw, string? container)
    {
        Provider = provider;
        _raw = string.IsNullOrWhiteSpace(raw) ? null : raw;
        Container = string.IsNullOrWhiteSpace(container) ? null : container;
    }

    public Provider Provider { get; }

    public bool Configured => _raw is not null;

    public string? Container { get; }

    public static Endpoint Resolve(Provider p, IReadOnlyDictionary<string, string?> environment) => new(
        p,
        environment.GetValueOrDefault(p == Provider.SqlServer ? SqlServerVariable : PostgreSqlVariable),
        environment.GetValueOrDefault(p == Provider.SqlServer ? SqlServerContainerVariable : PostgreSqlContainerVariable));

    public static string VariableFor(Provider p) => p == Provider.SqlServer ? SqlServerVariable : PostgreSqlVariable;

    /// <summary>
    /// A configured endpoint may name no database, the maintenance database, or the approved
    /// disposable database. Anything else is refused: the operator pointed at a real database.
    /// </summary>
    public void Validate()
    {
        var named = DatabaseNamed();
        if (string.IsNullOrEmpty(named)
            || string.Equals(named, Safety.MaintenanceDatabase(Provider), StringComparison.OrdinalIgnoreCase)
            || string.Equals(named, Safety.ApprovedDatabase(Provider), StringComparison.Ordinal))
            return;

        throw new SafetyRefusalException(
            $"{Provider}: {VariableFor(Provider)} names database '{named}'. It must name no database, " +
            $"'{Safety.MaintenanceDatabase(Provider)}', or '{Safety.ApprovedDatabase(Provider)}'.");
    }

    public string Describe()
    {
        if (_raw is null) return $"{Provider}: not configured ({VariableFor(Provider)} unset)";
        return Provider == Provider.SqlServer
            ? $"{Provider}: data source '{new SqlConnectionStringBuilder(_raw).DataSource}'"
            : $"{Provider}: host '{new NpgsqlConnectionStringBuilder(_raw).Host}' port {new NpgsqlConnectionStringBuilder(_raw).Port}";
    }

    public string AdminConnectionString() => WithDatabase(Safety.MaintenanceDatabase(Provider));

    public string TargetConnectionString()
    {
        Safety.AssertApprovedTarget(Provider, Safety.ApprovedDatabase(Provider));
        return WithDatabase(Safety.ApprovedDatabase(Provider));
    }

    private string? DatabaseNamed() => Provider == Provider.SqlServer
        ? new SqlConnectionStringBuilder(_raw).InitialCatalog
        : new NpgsqlConnectionStringBuilder(_raw).Database;

    private string WithDatabase(string database)
    {
        if (_raw is null) throw new InvalidOperationException($"{Provider} endpoint is not configured.");
        if (Provider == Provider.SqlServer)
            return new SqlConnectionStringBuilder(_raw) { InitialCatalog = database, Pooling = true }.ConnectionString;
        return new NpgsqlConnectionStringBuilder(_raw) { Database = database, Pooling = true }.ConnectionString;
    }

    /// <summary>
    /// Read-only container evidence via `docker inspect` — only when a container name is
    /// supplied. Never starts, stops, restarts or reconfigures anything.
    /// </summary>
    public string ContainerState()
    {
        if (Container is null) return $"{Provider}: container state not recorded ({(Provider == Provider.SqlServer ? SqlServerContainerVariable : PostgreSqlContainerVariable)} unset)";
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                ArgumentList = { "inspect", "--format", "{{.State.Status}} StartedAt={{.State.StartedAt}} RestartCount={{.RestartCount}}", Container },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15_000);
            return $"{Provider}: container '{Container}' {(process.ExitCode == 0 ? output : "not inspectable")}";
        }
        catch (Exception ex)
        {
            return $"{Provider}: container '{Container}' state unavailable ({ex.GetType().Name})";
        }
    }
}
