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

    /// <summary>
    /// The owner-approved endpoint identity, in non-secret form: a SQL Server data source
    /// (<c>host[\instance][,port]</c>) or a PostgreSQL <c>host:port</c>. REQUIRED before any live
    /// operation; the configured endpoint must match it exactly (see <see cref="EndpointLocality"/>).
    /// </summary>
    public const string SqlServerExpectedVariable = "ERP_PARITY_SQLSERVER_EXPECTED_DATASOURCE";
    public const string PostgreSqlExpectedVariable = "ERP_PARITY_POSTGRESQL_EXPECTED_HOSTPORT";

    private readonly string? _raw;
    private readonly string? _expected;

    private Endpoint(Provider provider, string? raw, string? container, string? expected)
    {
        Provider = provider;
        _raw = string.IsNullOrWhiteSpace(raw) ? null : raw;
        Container = string.IsNullOrWhiteSpace(container) ? null : container;
        _expected = string.IsNullOrWhiteSpace(expected) ? null : expected;
    }

    public Provider Provider { get; }

    public bool Configured => _raw is not null;

    public string? Container { get; }

    public static Endpoint Resolve(Provider p, IReadOnlyDictionary<string, string?> environment) => new(
        p,
        environment.GetValueOrDefault(p == Provider.SqlServer ? SqlServerVariable : PostgreSqlVariable),
        environment.GetValueOrDefault(p == Provider.SqlServer ? SqlServerContainerVariable : PostgreSqlContainerVariable),
        environment.GetValueOrDefault(ExpectedVariableFor(p)));

    public static string VariableFor(Provider p) => p == Provider.SqlServer ? SqlServerVariable : PostgreSqlVariable;

    public static string ExpectedVariableFor(Provider p) => p == Provider.SqlServer ? SqlServerExpectedVariable : PostgreSqlExpectedVariable;

    /// <summary>
    /// Fail-closed locality guard, run BEFORE any connection. Returns the matched, normalized
    /// endpoint identity (non-secret) for evidence. A match proves only that the configuration
    /// points at the owner-approved loopback endpoint — NOT that the server is disposable or
    /// owner-controlled; that remains an explicit owner confirmation for any live gate.
    /// </summary>
    public string ValidateLocality()
    {
        if (_raw is null) throw new InvalidOperationException($"{Provider} endpoint is not configured.");
        return EndpointLocality.Validate(Provider, _raw, _expected);
    }

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

/// <summary>
/// ENDPOINT LOCALITY (P2-a.2). Pure parsing — never resolves a name or opens a connection.
///
/// A configured endpoint may proceed only when
///   1. it parses with the provider's own connection-string builder,
///   2. it uses no routing the harness cannot reason about (protocol prefixes, LocalDB, named
///      pipes, Unix sockets, failover partners, multiple hosts, attach-file or user instances),
///   3. its host is a recognised loopback form, and
///   4. its normalized identity equals the owner-supplied expected value EXACTLY.
/// Anything else is a <see cref="SafetyRefusalException"/>. Only case and surrounding
/// whitespace are normalized; equivalent-looking forms (localhost vs 127.0.0.1, an explicit vs
/// a default port) are deliberately NOT treated as equal. Messages name host/instance/port
/// only — never the connection string or a credential.
/// </summary>
public static class EndpointLocality
{
    private static readonly HashSet<string> SqlServerLoopback = new(StringComparer.OrdinalIgnoreCase)
        { "localhost", "127.0.0.1", "::1", "(local)", "." };

    private static readonly HashSet<string> PostgreSqlLoopback = new(StringComparer.OrdinalIgnoreCase)
        { "localhost", "127.0.0.1", "::1" };

    private static readonly string[] SqlServerProtocolPrefixes = ["tcp:", "np:", "lpc:", "admin:", "via:"];

    public static string Validate(Provider p, string raw, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            throw new SafetyRefusalException(
                $"{p}: {Endpoint.ExpectedVariableFor(p)} is not set. The owner-approved endpoint is required before any live operation.");

        var actual = p == Provider.SqlServer ? SqlServerActual(raw) : PostgreSqlActual(raw);
        var approved = p == Provider.SqlServer
            ? NormalizeSqlServerDataSource(expected, $"{Endpoint.ExpectedVariableFor(p)}")
            : NormalizePostgreSqlHostPort(expected);

        if (!string.Equals(actual, approved, StringComparison.Ordinal))
            throw new SafetyRefusalException(
                $"{p}: configured endpoint '{actual}' does not match the approved endpoint '{approved}' ({Endpoint.ExpectedVariableFor(p)}).");
        return actual;
    }

    // ── SQL Server ───────────────────────────────────────────────────────────

    private static string SqlServerActual(string raw)
    {
        SqlConnectionStringBuilder builder;
        try { builder = new SqlConnectionStringBuilder(raw); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SafetyRefusalException($"SqlServer: unsupported connection-string syntax ({ex.GetType().Name}).");
        }

        if (!string.IsNullOrWhiteSpace(builder.FailoverPartner))
            throw new SafetyRefusalException("SqlServer: a failover partner is not allowed.");
        if (builder.MultiSubnetFailover)
            throw new SafetyRefusalException("SqlServer: MultiSubnetFailover is not allowed.");
        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename))
            throw new SafetyRefusalException("SqlServer: AttachDBFilename is not allowed.");
        if (builder.UserInstance)
            throw new SafetyRefusalException("SqlServer: User Instance is not allowed.");

        return NormalizeSqlServerDataSource(builder.DataSource, "configured data source");
    }

    /// <summary><c>host[\instance][,port]</c> with a loopback host, normalized to lower-case host and upper-case instance.</summary>
    public static string NormalizeSqlServerDataSource(string? dataSource, string what)
    {
        var ds = (dataSource ?? string.Empty).Trim();
        if (ds.Length == 0)
            throw new SafetyRefusalException($"SqlServer: {what} is empty.");
        if (ds.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            throw new SafetyRefusalException($"SqlServer: {what} uses LocalDB, which is not allowed.");
        if (ds.Contains('/') || ds.StartsWith(@"\\", StringComparison.Ordinal) || ds.Contains("pipe", StringComparison.OrdinalIgnoreCase))
            throw new SafetyRefusalException($"SqlServer: {what} uses named-pipe or path syntax, which is not allowed.");
        if (SqlServerProtocolPrefixes.Any(prefix => ds.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new SafetyRefusalException($"SqlServer: {what} uses a protocol prefix, which is not allowed.");

        var portSplit = ds.Split(',');
        if (portSplit.Length > 2)
            throw new SafetyRefusalException($"SqlServer: {what} '{ds}' is ambiguous (more than one ',').");
        string? port = null;
        if (portSplit.Length == 2)
        {
            port = portSplit[1].Trim();
            if (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                || number is < 1 or > 65535 || port != number.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new SafetyRefusalException($"SqlServer: {what} has an invalid port '{port}'.");
        }

        var instanceSplit = portSplit[0].Trim().Split('\\');
        if (instanceSplit.Length > 2)
            throw new SafetyRefusalException($"SqlServer: {what} '{ds}' is ambiguous (more than one '\\').");
        var host = instanceSplit[0].Trim();
        var instance = instanceSplit.Length == 2 ? instanceSplit[1].Trim() : null;
        if (instance is not null && !System.Text.RegularExpressions.Regex.IsMatch(instance, "^[A-Za-z0-9_$#-]{1,16}$"))
            throw new SafetyRefusalException($"SqlServer: {what} has an unsupported instance name.");
        if (host.Contains(':') && !string.Equals(host, "::1", StringComparison.Ordinal))
            throw new SafetyRefusalException($"SqlServer: {what} host '{host}' is ambiguous.");
        if (!SqlServerLoopback.Contains(host))
            throw new SafetyRefusalException($"SqlServer: {what} host '{host}' is not a recognised loopback host.");

        return host.ToLowerInvariant()
               + (instance is null ? string.Empty : @"\" + instance.ToUpperInvariant())
               + (port is null ? string.Empty : "," + port);
    }

    // ── PostgreSQL ───────────────────────────────────────────────────────────

    private static string PostgreSqlActual(string raw)
    {
        NpgsqlConnectionStringBuilder builder;
        try { builder = new NpgsqlConnectionStringBuilder(raw); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SafetyRefusalException($"PostgreSql: unsupported connection-string syntax ({ex.GetType().Name}).");
        }

        var host = (builder.Host ?? string.Empty).Trim();
        if (host.Length == 0)
            throw new SafetyRefusalException("PostgreSql: Host is not set.");
        if (host.Contains(','))
            throw new SafetyRefusalException("PostgreSql: multiple hosts are not allowed.");
        if (host.StartsWith('/') || host.StartsWith('@') || host.Contains('\\'))
            throw new SafetyRefusalException("PostgreSql: Unix-socket or path hosts are not allowed.");
        if (builder.LoadBalanceHosts)
            throw new SafetyRefusalException("PostgreSql: LoadBalanceHosts is not allowed.");
        if (builder.TargetSessionAttributes is not null)
            throw new SafetyRefusalException("PostgreSql: Target Session Attributes is not allowed.");
        if (host.Contains(':') && !string.Equals(host, "::1", StringComparison.Ordinal))
            throw new SafetyRefusalException($"PostgreSql: host '{host}' is ambiguous (port inside Host).");
        if (!PostgreSqlLoopback.Contains(host))
            throw new SafetyRefusalException($"PostgreSql: host '{host}' is not a recognised loopback host.");
        if (builder.Port is < 1 or > 65535)
            throw new SafetyRefusalException($"PostgreSql: invalid port {builder.Port}.");

        return Format(host, builder.Port);
    }

    /// <summary><c>host:port</c> (or <c>[::1]:port</c>) with a loopback host and an EXPLICIT port.</summary>
    public static string NormalizePostgreSqlHostPort(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        string host, port;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0) throw new SafetyRefusalException($"PostgreSql: {Endpoint.PostgreSqlExpectedVariable} must be host:port.");
            host = text[1..close];
            port = text[(close + 2)..];
        }
        else
        {
            var parts = text.Split(':');
            if (parts.Length != 2) throw new SafetyRefusalException($"PostgreSql: {Endpoint.PostgreSqlExpectedVariable} must be host:port.");
            host = parts[0].Trim();
            port = parts[1].Trim();
        }

        if (!PostgreSqlLoopback.Contains(host))
            throw new SafetyRefusalException($"PostgreSql: {Endpoint.PostgreSqlExpectedVariable} host '{host}' is not a recognised loopback host.");
        if (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 65535 || port != number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new SafetyRefusalException($"PostgreSql: {Endpoint.PostgreSqlExpectedVariable} has an invalid port '{port}'.");
        return Format(host, number);
    }

    private static string Format(string host, int port) =>
        (host == "::1" ? "[::1]" : host.ToLowerInvariant()) + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
