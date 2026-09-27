using ERP.Infrastructure.Tenant;
using ERP.Persistence.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// Tests for TenantProvisioningService provider selection (slice P1-e / DB-04).
///
/// BuildDbContextOptions used to fall through to SQL Server for ANY value that was not
/// "PostgreSql" — so a typo, or a null, would quietly provision the wrong engine. It now
/// delegates recognition to DatabaseProviderResolver.TryParse and refuses what it does not
/// recognise.
///
/// TEST SEAM
/// =========
/// BuildDbContextOptions is private, and it is deliberately NOT being widened for tests.
/// It does not need to be: both public methods wrap it in try/catch and surface the
/// exception message inside Result.Failure, so fail-closed behaviour is fully observable
/// through the public API. Every assertion below goes through that real, existing seam.
///
/// The service is a DI-only dead primitive today — zero invocations, no HTTP/CLI/hosted-service
/// reachability — so these tests pin behaviour that must be safe IF it is ever re-exposed.
/// That is the whole reason DB-04 matters here.
///
/// NOTE ON THE SUCCESS PATH: a recognised provider proceeds to MigrateAsync, which needs a
/// live database. These tests therefore assert on the REJECTION path only, and prove
/// rejection is provider-vocabulary rejection rather than a database error. Positive
/// provider-to-seam mapping is covered where it is observable without a database — the
/// AppDb and Master design-time tests, which exercise the same enum and the same two seams.
/// </summary>
public class TenantProvisioningProviderTests
{
    // Rejected-provider tests never reach a connection attempt at all.
    private const string AnyConnectionString =
        "Server=localhost;Database=ERP_Tenant_Test;Trusted_Connection=true;TrustServerCertificate=true;";

    // For the two tests that DO get past provider selection, the connection must be
    // guaranteed unreachable. ProvisionTenantDatabaseAsync calls MigrateAsync, which
    // CREATES the database — pointing these at a live local server would leave a real
    // database behind as a test side effect. Port 1 has no listener, and a 1-second timeout
    // keeps the tests fast.
    private const string UnreachableSqlServerConnectionString =
        "Server=127.0.0.1,1;Database=ERP_Never_Created;Connect Timeout=1;TrustServerCertificate=true;";
    private const string UnreachablePostgreSqlConnectionString =
        "Host=127.0.0.1;Port=1;Database=erp_never_created;Username=nobody;Password=not-a-real-password;Timeout=1";

    private static TenantProvisioningService CreateService() =>
        new(NullLogger<TenantProvisioningService>.Instance);

    // ── Rejected values fail closed, through the real public API ──────────────

    public static TheoryData<string?> RejectedProviders() =>
    [
        null,
        "",
        " ",
        "   ",
        "\t",
        "Oracle",
        "MySql",
        "Sqlite",
        "Postgres",
        "SqlServer2022",
        "Sql Server",
        "nonsense",
    ];

    [Theory]
    [MemberData(nameof(RejectedProviders))]
    public async Task Provision_RejectedProvider_FailsClosed(string? dbProvider)
    {
        var result = await CreateService()
            .ProvisionTenantDatabaseAsync(AnyConnectionString, dbProvider!);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.ProvisionFailed", result.Error.Code);
        Assert.Contains("Invalid tenant database provider", result.Error.Message);
    }

    [Theory]
    [MemberData(nameof(RejectedProviders))]
    public async Task Migrate_RejectedProvider_FailsClosed(string? dbProvider)
    {
        var result = await CreateService()
            .MigrateTenantDatabaseAsync(AnyConnectionString, dbProvider!);

        Assert.True(result.IsFailure);
        Assert.Equal("Tenant.MigrationFailed", result.Error.Code);
        Assert.Contains("Invalid tenant database provider", result.Error.Message);
    }

    // ── The specific regression: no invalid value yields SQL Server options ───

    // Previously "Oracle" silently produced SQL Server options and the call proceeded to
    // touch a database. Now it never gets that far — failure is provider REJECTION, not a
    // connection or migration error.
    [Theory]
    [InlineData("Oracle")]
    [InlineData("Postgres")]
    [InlineData("nonsense")]
    [InlineData("")]
    public async Task InvalidProvider_NeverProducesSqlServerOptions(string dbProvider)
    {
        var result = await CreateService()
            .ProvisionTenantDatabaseAsync(AnyConnectionString, dbProvider);

        Assert.True(result.IsFailure);

        // Rejected by the provider vocabulary...
        Assert.Contains("Invalid tenant database provider", result.Error.Message);

        // ...and NOT by anything downstream that would imply a provider was chosen and a
        // connection attempted.
        Assert.DoesNotContain("login", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("network", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("server was not found", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timeout", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── The diagnostic is provisioning-context appropriate ───────────────────

    [Fact]
    public async Task FailureDiagnostic_NamesTheSuppliedValue()
    {
        var result = await CreateService()
            .ProvisionTenantDatabaseAsync(AnyConnectionString, "Oracle");

        Assert.Contains("'Oracle'", result.Error.Message);
    }

    [Fact]
    public async Task FailureDiagnostic_RepresentsNullClearly()
    {
        var result = await CreateService()
            .ProvisionTenantDatabaseAsync(AnyConnectionString, null!);

        Assert.Contains("null", result.Error.Message);
        // Must not render null as an empty pair of quotes, which would be
        // indistinguishable from an empty string.
        Assert.DoesNotContain("''", result.Error.Message);
    }

    [Fact]
    public async Task FailureDiagnostic_DistinguishesEmptyFromWhitespace()
    {
        var empty = await CreateService().ProvisionTenantDatabaseAsync(AnyConnectionString, "");
        var whitespace = await CreateService().ProvisionTenantDatabaseAsync(AnyConnectionString, "   ");

        Assert.Contains("empty", empty.Error.Message);
        Assert.Contains("whitespace", whitespace.Error.Message);
        Assert.NotEqual(empty.Error.Message, whitespace.Error.Message);
    }

    [Fact]
    public async Task FailureDiagnostic_ListsSupportedValues()
    {
        var result = await CreateService()
            .ProvisionTenantDatabaseAsync(AnyConnectionString, "Oracle");

        Assert.Contains("SqlServer", result.Error.Message);
        Assert.Contains("PostgreSql", result.Error.Message);
        Assert.Contains(DatabaseProviderResolver.SupportedProviders, result.Error.Message);
    }

    // The point of Option 2: this path must NOT tell the operator to edit a configuration
    // key it never read. The value arrives as a method argument.
    [Theory]
    [MemberData(nameof(RejectedProviders))]
    public async Task FailureDiagnostic_DoesNotInstructSettingDatabaseProvider(string? dbProvider)
    {
        var result = await CreateService()
            .ProvisionTenantDatabaseAsync(AnyConnectionString, dbProvider!);

        Assert.DoesNotContain(DatabaseProviderResolver.ConfigurationKey, result.Error.Message);
        Assert.DoesNotContain("appsettings", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment variable", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Recognised values are accepted by the vocabulary ─────────────────────

    // A recognised provider must get PAST provider selection. It then fails on the database
    // (there is none), which is a different failure entirely — proving the value was
    // accepted rather than rejected. This asserts only the absence of provider rejection,
    // so it does not depend on any particular database error text.
    [Theory]
    [InlineData("SqlServer")]
    [InlineData("sqlserver")]
    [InlineData("SQLSERVER")]
    [InlineData("  SqlServer  ")]
    public async Task RecognisedSqlServerValues_PassProviderSelection(string dbProvider)
    {
        var result = await CreateService()
            .ProvisionTenantDatabaseAsync(UnreachableSqlServerConnectionString, dbProvider);

        Assert.DoesNotContain("Invalid tenant database provider", result.Error?.Message ?? string.Empty);
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    [InlineData("  PostgreSql  ")]
    public async Task RecognisedPostgreSqlValues_PassProviderSelection(string dbProvider)
    {
        var result = await CreateService()
            .ProvisionTenantDatabaseAsync(UnreachablePostgreSqlConnectionString, dbProvider);

        Assert.DoesNotContain("Invalid tenant database provider", result.Error?.Message ?? string.Empty);
    }
}
