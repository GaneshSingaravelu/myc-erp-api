using ERP.DbMigrator;
using ERP.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// Tests for AppDbContext (tenant database) DESIGN-TIME provider selection
/// (slice P1-e / DB-04).
///
/// AppDbContextFactory used to take its provider from Tenants[0].DbProvider, and silently
/// fall back to SQL Server for a missing or unrecognised value. That is why PostgreSQL
/// tenant migrations were never generated: the first configured tenant uses SQL Server, so
/// switching engine meant reordering a configuration array.
///
/// Provider authority is now the deployment-level Database:Provider, parsed by the shared
/// DatabaseProviderResolver. The tenant CONNECTION STRING still comes from the first
/// configured tenant entry - that selection is deliberately unchanged by this slice, and
/// several tests below pin it so a future change cannot drift it silently.
///
/// These tests exercise BuildOptions against an in-memory IConfiguration. No filesystem
/// access and no database connection: building options and reading ProviderName never
/// opens one.
/// </summary>
public class AppDesignTimeProviderSelectionTests
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // Syntactically valid connection strings. Never connected to; they only need to be
    // parseable by the respective provider when options are built.
    private const string SqlServerConnectionString =
        "Server=localhost;Database=ERP_Tenant_Test;Trusted_Connection=true;TrustServerCertificate=true;";
    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=erp_tenant_test;Username=erp_test;Password=not-a-real-password";

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a configuration shaped like the host's appsettings: a Tenants array plus the
    /// deployment-level Database:Provider.
    /// </summary>
    private static IConfiguration BuildConfiguration(
        string? databaseProvider,
        string tenantConnectionString,
        string? firstTenantDbProvider = null,
        bool includeTenant = true)
    {
        var values = new Dictionary<string, string?>();

        if (databaseProvider is not null)
            values["Database:Provider"] = databaseProvider;

        if (includeTenant)
        {
            values["Tenants:0:Identifier"] = "dev-tenant";
            values["Tenants:0:ConnectionString"] = tenantConnectionString;

            // Present only when a test is specifically proving it has no influence.
            if (firstTenantDbProvider is not null)
                values["Tenants:0:DbProvider"] = firstTenantDbProvider;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Models AddJsonFile(...).AddEnvironmentVariables() layering: later source wins.</summary>
    private static IConfiguration BuildLayeredConfiguration(
        string? jsonProvider, string? environmentProvider, string tenantConnectionString)
    {
        var json = new Dictionary<string, string?>
        {
            ["Tenants:0:Identifier"] = "dev-tenant",
            ["Tenants:0:ConnectionString"] = tenantConnectionString,
        };
        if (jsonProvider is not null)
            json["Database:Provider"] = jsonProvider;

        var environment = new Dictionary<string, string?>();
        if (environmentProvider is not null)
            environment["Database:Provider"] = environmentProvider;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(json)
            .AddInMemoryCollection(environment)
            .Build();
    }

    private static string ProviderNameOf(DbContextOptions<AppDbContext> options)
    {
        using var context = new AppDbContext(options);
        return context.Database.ProviderName!;
    }

    private static RelationalOptionsExtension RelationalExtension(
        DbContextOptions<AppDbContext> options) =>
        options.Extensions.OfType<RelationalOptionsExtension>().Single();

    // ── Provider selection follows Database:Provider ──────────────────────────

    [Fact]
    public void SqlServer_SelectsSqlServerProvider()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration("SqlServer", SqlServerConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    [Fact]
    public void PostgreSql_SelectsNpgsqlProvider()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration("PostgreSql", PostgreSqlConnectionString));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
    }

    // ── Correct tenant migrations assembly per provider ───────────────────────

    [Fact]
    public void SqlServer_UsesSqlServerMigrationsAssembly()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration("SqlServer", SqlServerConnectionString));

        var migrationsAssembly = RelationalExtension(options).MigrationsAssembly;

        Assert.NotNull(migrationsAssembly);
        Assert.StartsWith("ERP.Persistence.SqlServer", migrationsAssembly);
        Assert.DoesNotContain("PostgreSql", migrationsAssembly);
    }

    [Fact]
    public void PostgreSql_UsesPostgreSqlMigrationsAssembly()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration("PostgreSql", PostgreSqlConnectionString));

        var migrationsAssembly = RelationalExtension(options).MigrationsAssembly;

        Assert.NotNull(migrationsAssembly);
        Assert.StartsWith("ERP.Persistence.PostgreSql", migrationsAssembly);
        Assert.DoesNotContain("SqlServer", migrationsAssembly);
    }

    // Proves selection delegates to the seam rather than re-implementing provider config.
    [Theory]
    [InlineData("SqlServer", SqlServerConnectionString)]
    [InlineData("PostgreSql", PostgreSqlConnectionString)]
    public void ProviderSeam_ConfigurationIsApplied(string provider, string connectionString)
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration(provider, connectionString));

        Assert.Equal(30, RelationalExtension(options).CommandTimeout);
    }

    // ── Case-insensitive matching, per the approved rule ──────────────────────

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("SQLSERVER")]
    [InlineData("SqlSerVer")]
    public void SqlServer_SelectionIsCaseInsensitive(string configuredValue)
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration(configuredValue, SqlServerConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    [InlineData("PostgreSQL")]
    public void PostgreSql_SelectionIsCaseInsensitive(string configuredValue)
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration(configuredValue, PostgreSqlConnectionString));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
    }

    // ── Fail closed ──────────────────────────────────────────────────────────

    [Fact]
    public void MissingProvider_FailsClosed()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AppDbContextFactory.BuildOptions(
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
            () => AppDbContextFactory.BuildOptions(
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
            () => AppDbContextFactory.BuildOptions(
                BuildConfiguration(configuredValue, SqlServerConnectionString)));

        Assert.Contains("Database:Provider", exception.Message);
    }

    // The specific regression that would resurrect the old silent fallback.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Oracle")]
    [InlineData("nonsense")]
    public void InvalidProvider_NeverYieldsOptions(string? configuredValue)
    {
        var exception = Record.Exception(
            () => AppDbContextFactory.BuildOptions(
                BuildConfiguration(configuredValue, SqlServerConnectionString)));

        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    // ── Tenants[0].DbProvider cannot influence provider selection ────────────

    [Fact]
    public void FirstTenantProvider_DoesNotOverrideDatabaseProvider()
    {
        // Database:Provider says SqlServer; the first tenant says PostgreSql.
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration("SqlServer", SqlServerConnectionString, firstTenantDbProvider: "PostgreSql"));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
        Assert.StartsWith("ERP.Persistence.SqlServer", RelationalExtension(options).MigrationsAssembly!);
    }

    [Fact]
    public void FirstTenantProvider_DoesNotOverrideDatabaseProvider_Inverse()
    {
        // And the other way round, so the test cannot pass by coincidence of a default.
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration("PostgreSql", PostgreSqlConnectionString, firstTenantDbProvider: "SqlServer"));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
        Assert.StartsWith("ERP.Persistence.PostgreSql", RelationalExtension(options).MigrationsAssembly!);
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("PostgreSql")]
    public void FirstTenantProvider_CannotSubstituteForAMissingDatabaseProvider(string firstTenantDbProvider)
    {
        // With Database:Provider absent, a populated Tenants[0].DbProvider must NOT rescue
        // it. This is exactly the old behaviour, and it must stay gone.
        Assert.Throws<InvalidOperationException>(
            () => AppDbContextFactory.BuildOptions(
                BuildConfiguration(null, SqlServerConnectionString, firstTenantDbProvider: firstTenantDbProvider)));
    }

    // The provider is never inferred from connection-string shape either.
    [Fact]
    public void ConnectionStringSyntax_DoesNotInfluenceProviderSelection()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildConfiguration("SqlServer", PostgreSqlConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    // ── Tenant connection-string behaviour is UNCHANGED by this slice ─────────

    [Fact]
    public void NoTenantConfigured_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AppDbContextFactory.BuildOptions(
                BuildConfiguration("SqlServer", SqlServerConnectionString, includeTenant: false)));

        Assert.Contains("No tenants configured", exception.Message);
    }

    [Fact]
    public void FirstTenantWithoutConnectionString_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "SqlServer",
                ["Tenants:0:Identifier"] = "dev-tenant",
                // ConnectionString deliberately absent
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => AppDbContextFactory.BuildOptions(configuration));

        Assert.Contains("ConnectionString", exception.Message);
    }

    // The FIRST tenant's connection string is still the one used - tenant selection was
    // not redesigned. Proven by giving the two tenants different databases and checking
    // which one lands in the options.
    [Fact]
    public void FirstTenantConnectionString_IsStillTheOneUsed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "SqlServer",
                ["Tenants:0:Identifier"] = "first-tenant",
                ["Tenants:0:ConnectionString"] =
                    "Server=localhost;Database=ERP_First;Trusted_Connection=true;TrustServerCertificate=true;",
                ["Tenants:1:Identifier"] = "second-tenant",
                ["Tenants:1:ConnectionString"] =
                    "Server=localhost;Database=ERP_Second;Trusted_Connection=true;TrustServerCertificate=true;",
            })
            .Build();

        var options = AppDbContextFactory.BuildOptions(configuration);
        var connectionString = RelationalExtension(options).ConnectionString;

        Assert.NotNull(connectionString);
        Assert.Contains("ERP_First", connectionString);
        Assert.DoesNotContain("ERP_Second", connectionString);
    }

    // ── Configuration precedence: environment overrides JSON ─────────────────

    [Fact]
    public void EnvironmentProvider_OverridesJsonProvider_ToPostgreSql()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildLayeredConfiguration("SqlServer", "PostgreSql", PostgreSqlConnectionString));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
        Assert.StartsWith("ERP.Persistence.PostgreSql", RelationalExtension(options).MigrationsAssembly!);
    }

    [Fact]
    public void EnvironmentProvider_OverridesJsonProvider_ToSqlServer()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildLayeredConfiguration("PostgreSql", "SqlServer", SqlServerConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    [Fact]
    public void NoEnvironmentOverride_FallsBackToJsonProvider()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildLayeredConfiguration("SqlServer", null, SqlServerConnectionString));

        Assert.Equal(SqlServerProviderName, ProviderNameOf(options));
    }

    [Fact]
    public void EnvironmentProvider_CanSupplyAnAbsentJsonProvider()
    {
        var options = AppDbContextFactory.BuildOptions(
            BuildLayeredConfiguration(null, "PostgreSql", PostgreSqlConnectionString));

        Assert.Equal(PostgreSqlProviderName, ProviderNameOf(options));
    }

    // A bad override is NOT rescued by a valid JSON value beneath it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Oracle")]
    [InlineData("nonsense")]
    public void InvalidEnvironmentProvider_StillFailsClosed(string environmentProvider)
    {
        Assert.Throws<InvalidOperationException>(
            () => AppDbContextFactory.BuildOptions(
                BuildLayeredConfiguration("SqlServer", environmentProvider, SqlServerConnectionString)));
    }
}
