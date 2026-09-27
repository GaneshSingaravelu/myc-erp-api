using ERP.DbMigrator;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// Tests for MasterDbContext DESIGN-TIME provider selection (slice P1-d / DB-01).
///
/// MasterDbContextFactory is what `dotnet ef` uses for the Master database. It hardcoded
/// UseSqlServer, so even after P1-c made the RUNTIME provider configurable, `dotnet ef`
/// still resolved SQL Server regardless of Database:Provider — migrations would have been
/// generated for the wrong engine on a PostgreSQL deployment.
///
/// These tests exercise MasterDbContextFactory.BuildOptions against an in-memory
/// IConfiguration. No filesystem access, no database connection: building options and
/// reading ProviderName never opens a connection.
///
/// The most important assertions here are the negative ones. Design-time provider
/// selection must come from Database:Provider alone — never from connection-string syntax,
/// never from Tenants[0].DbProvider, and never from a fallback.
/// </summary>
public class MasterDesignTimeProviderSelectionTests
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // Syntactically valid connection strings for each engine. Never connected to; they
    // only need to be parseable by the respective provider when options are built.
    private const string SqlServerConnectionString =
        "Server=localhost;Database=ERP_Master_Test;Trusted_Connection=true;TrustServerCertificate=true;";
    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=erp_master_test;Username=erp_test;Password=not-a-real-password";

    // ── Harness ───────────────────────────────────────────────────────────────

    private static IConfiguration BuildConfiguration(
        string? provider,
        string connectionString,
        string? firstTenantDbProvider = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:MasterDb"] = connectionString,
        };

        // A null provider models the key being ABSENT from effective configuration.
        if (provider is not null)
            values["Database:Provider"] = provider;

        // Optional: populate the Tenants array the way appsettings.Development.json does,
        // so tests can prove design-time selection ignores it.
        if (firstTenantDbProvider is not null)
        {
            values["Tenants:0:Identifier"] = "dev-tenant";
            values["Tenants:0:ConnectionString"] = "Server=localhost;Database=ERP_Dev;";
            values["Tenants:0:DbProvider"] = firstTenantDbProvider;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string ProviderNameOf(DbContextOptions<MasterDbContext> options)
    {
        using var context = new MasterDbContext(options);
        return context.Database.ProviderName!;
    }

    private static string? MigrationsAssemblyOf(DbContextOptions<MasterDbContext> options) =>
        options.Extensions.OfType<RelationalOptionsExtension>().Single().MigrationsAssembly;

    // ── 1 & 2. Provider selection follows Database:Provider ───────────────────

    [Fact]
    public void SqlServer_SelectsSqlServerProvider()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration("SqlServer", SqlServerConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    [Fact]
    public void PostgreSql_SelectsNpgsqlProvider()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration("PostgreSql", PostgreSqlConnectionString));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
    }

    // ── 3 & 4. Correct Master migrations assembly per provider ───────────────

    [Fact]
    public void SqlServer_UsesSqlServerMigrationsAssembly()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration("SqlServer", SqlServerConnectionString));

        var migrationsAssembly = MigrationsAssemblyOf(options);

        Assert.NotNull(migrationsAssembly);
        Assert.StartsWith("ERP.Persistence.SqlServer", migrationsAssembly);
        Assert.DoesNotContain("PostgreSql", migrationsAssembly);
    }

    [Fact]
    public void PostgreSql_UsesPostgreSqlMigrationsAssembly()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration("PostgreSql", PostgreSqlConnectionString));

        var migrationsAssembly = MigrationsAssemblyOf(options);

        Assert.NotNull(migrationsAssembly);
        Assert.StartsWith("ERP.Persistence.PostgreSql", migrationsAssembly);
        Assert.DoesNotContain("SqlServer", migrationsAssembly);
    }

    // ── 5. Case-insensitive matching, per the approved rule ──────────────────

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("SQLSERVER")]
    [InlineData("SqlSerVer")]
    public void SqlServer_SelectionIsCaseInsensitive(string configuredValue)
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration(configuredValue, SqlServerConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    [InlineData("PostgreSQL")]
    public void PostgreSql_SelectionIsCaseInsensitive(string configuredValue)
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration(configuredValue, PostgreSqlConnectionString));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
    }

    // ── 6, 7 & 8. Fail closed ───────────────────────────────────────────────

    [Fact]
    public void MissingProvider_FailsClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => MasterDbContextFactory.BuildOptions(
                BuildConfiguration(null, SqlServerConnectionString)));

        Assert.Contains("Database:Provider", exception.Message);
        Assert.Contains("SqlServer", exception.Message);
        Assert.Contains("PostgreSql", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void BlankProvider_FailsClosed(string configuredValue)
    {
        Assert.Throws<InvalidOperationException>(
            () => MasterDbContextFactory.BuildOptions(
                BuildConfiguration(configuredValue, SqlServerConnectionString)));
    }

    [Theory]
    [InlineData("Oracle")]
    [InlineData("MySql")]
    [InlineData("Sqlite")]
    [InlineData("Postgres")]
    [InlineData("SqlServer2022")]
    [InlineData("nonsense")]
    public void UnsupportedProvider_FailsClosed(string configuredValue)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => MasterDbContextFactory.BuildOptions(
                BuildConfiguration(configuredValue, SqlServerConnectionString)));

        Assert.Contains("Database:Provider", exception.Message);
    }

    // ── 9. Selection does NOT depend on Tenants[0].DbProvider ────────────────

    // AppDbContextFactory reads Tenants[0].DbProvider for TENANT migrations. That is a
    // per-tenant column describing a tenant's business database and cannot describe the
    // Master database, so the Master factory must ignore it entirely.

    [Fact]
    public void FirstTenantProvider_DoesNotOverrideDatabaseProvider()
    {
        // Database:Provider says SqlServer; the first tenant says PostgreSql.
        // Database:Provider must win.
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration("SqlServer", SqlServerConnectionString, firstTenantDbProvider: "PostgreSql"));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    [Fact]
    public void FirstTenantProvider_DoesNotOverrideDatabaseProvider_Inverse()
    {
        // And the other way round, so the test cannot pass by coincidence of defaults.
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration("PostgreSql", PostgreSqlConnectionString, firstTenantDbProvider: "SqlServer"));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
    }

    [Fact]
    public void FirstTenantProvider_CannotSubstituteForAMissingDatabaseProvider()
    {
        // With Database:Provider absent, a populated Tenants array must NOT rescue it.
        // This is the specific regression that would silently resurrect the old
        // Tenants[0] behaviour for the Master database.
        Assert.Throws<InvalidOperationException>(
            () => MasterDbContextFactory.BuildOptions(
                BuildConfiguration(null, SqlServerConnectionString, firstTenantDbProvider: "SqlServer")));
    }

    // ── 10. No silent SQL Server fallback ───────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Oracle")]
    [InlineData("nonsense")]
    public void InvalidProvider_NeverYieldsOptions(string? configuredValue)
    {
        var exception = Record.Exception(
            () => MasterDbContextFactory.BuildOptions(
                BuildConfiguration(configuredValue, SqlServerConnectionString)));

        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    // The provider is never inferred from connection-string shape: a PostgreSQL-looking
    // connection string with Database:Provider = SqlServer still selects SQL Server.
    [Fact]
    public void ConnectionStringSyntax_DoesNotInfluenceProviderSelection()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildConfiguration("SqlServer", PostgreSqlConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    // ── Design time agrees with runtime ─────────────────────────────────────

    // The same Database:Provider value must produce the same engine at design time as it
    // does at runtime. If these ever diverge, `dotnet ef` would generate migrations for a
    // different engine than the application runs on.
    [Theory]
    [InlineData("SqlServer", SqlServerConnectionString, SqlServerProviderName)]
    [InlineData("PostgreSql", PostgreSqlConnectionString, PostgreSqlProviderName)]
    public void DesignTimeProvider_MatchesRuntimeProvider(
        string provider, string connectionString, string expectedProviderName)
    {
        var designTime = MasterDbContextFactory.BuildOptions(
            BuildConfiguration(provider, connectionString));

        Assert.Equal(expectedProviderName, ProviderNameOf(designTime));
    }

    // ── Configuration precedence: environment overrides JSON ─────────────────

    // CreateDbContext layers AddEnvironmentVariables() AFTER the JSON sources, so
    // Database__Provider can override the JSON-configured provider for a `dotnet ef`
    // invocation without editing the shared host appsettings.json.
    //
    // These tests model that layering with the same ConfigurationBuilder semantics rather
    // than mutating real process environment variables, which would leak across the test
    // run. The end-to-end proof that the real CLI honours the override is the `dotnet ef`
    // evidence recorded with this slice.

    private static IConfiguration BuildLayeredConfiguration(
        string? jsonProvider, string? environmentProvider, string connectionString)
    {
        var json = new Dictionary<string, string?>
        {
            ["ConnectionStrings:MasterDb"] = connectionString,
        };
        if (jsonProvider is not null)
            json["Database:Provider"] = jsonProvider;

        var environment = new Dictionary<string, string?>();
        if (environmentProvider is not null)
            environment["Database:Provider"] = environmentProvider;

        // Second source added wins, mirroring AddJsonFile(...).AddEnvironmentVariables().
        return new ConfigurationBuilder()
            .AddInMemoryCollection(json)
            .AddInMemoryCollection(environment)
            .Build();
    }

    [Fact]
    public void EnvironmentProvider_OverridesJsonProvider_ToPostgreSql()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildLayeredConfiguration(
                jsonProvider: "SqlServer",
                environmentProvider: "PostgreSql",
                PostgreSqlConnectionString));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
        Assert.StartsWith("ERP.Persistence.PostgreSql", MigrationsAssemblyOf(options)!);
    }

    // The inverse, so the test cannot pass by coincidence of a default.
    [Fact]
    public void EnvironmentProvider_OverridesJsonProvider_ToSqlServer()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildLayeredConfiguration(
                jsonProvider: "PostgreSql",
                environmentProvider: "SqlServer",
                SqlServerConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
        Assert.StartsWith("ERP.Persistence.SqlServer", MigrationsAssemblyOf(options)!);
    }

    [Fact]
    public void NoEnvironmentOverride_FallsBackToJsonProvider()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildLayeredConfiguration(
                jsonProvider: "SqlServer",
                environmentProvider: null,
                SqlServerConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    // An environment value can SUPPLY the key when JSON omits it entirely.
    [Fact]
    public void EnvironmentProvider_CanSupplyAnAbsentJsonProvider()
    {
        var options = MasterDbContextFactory.BuildOptions(
            BuildLayeredConfiguration(
                jsonProvider: null,
                environmentProvider: "PostgreSql",
                PostgreSqlConnectionString));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
    }

    // Overriding does not weaken fail-closed: a bad environment value still throws, and is
    // NOT rescued by a valid JSON value underneath it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Oracle")]
    [InlineData("nonsense")]
    public void InvalidEnvironmentProvider_StillFailsClosed(string environmentProvider)
    {
        Assert.Throws<InvalidOperationException>(
            () => MasterDbContextFactory.BuildOptions(
                BuildLayeredConfiguration(
                    jsonProvider: "SqlServer",
                    environmentProvider: environmentProvider,
                    SqlServerConnectionString)));
    }

    // A connection string is still required — this guard predates P1-d and must survive.
    [Fact]
    public void MissingConnectionString_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "SqlServer",
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => MasterDbContextFactory.BuildOptions(configuration));

        Assert.Contains("MasterDb", exception.Message);
    }
}
