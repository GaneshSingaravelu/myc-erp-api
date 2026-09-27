using ERP.Infrastructure.Extensions;
using ERP.Persistence.Context;
using ERP.Persistence.Extensions;
using ERP.Persistence.Interceptors;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// Tests for MasterDbContext RUNTIME provider selection (slice P1-c / DB-01).
///
/// MasterDbContext was previously registered with a hardcoded UseSqlServer call, so a
/// PostgreSQL deployment could not run its Master/Identity database at all. Registration
/// now selects between the two existing provider seams according to Database:Provider.
///
/// These tests build the real DI container and inspect the resulting
/// DbContextOptions&lt;MasterDbContext&gt;. No database is contacted — building options and
/// reading ProviderName never opens a connection — so they run anywhere the unit suite
/// runs.
///
/// NOTE ON LIFETIME: the options object keeps a reference to the application service
/// provider, so every test holds the container open for as long as it inspects the
/// options. Disposing the container first throws ObjectDisposedException.
///
/// The interceptor assertions are the important regression guard: provider switching must
/// not drop AuditableEntityInterceptor (which populates CreatedAt/LastModifiedAt/DeletedAt
/// across the whole Master schema) or DomainEventDispatcherInterceptor. Losing either
/// would be silent — audit columns would simply stop being filled.
/// </summary>
public class MasterProviderSelectionTests
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

    private static IConfiguration BuildConfiguration(string? provider, string connectionString)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:MasterDb"] = connectionString,
        };

        // A null provider models the key being ABSENT from effective configuration,
        // which must fail closed rather than defaulting.
        if (provider is not null)
            values["Database:Provider"] = provider;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();

        // AddPersistence registers the two interceptors that AddInfrastructure resolves.
        services.AddPersistence();

        // DomainEventDispatcherInterceptor takes an IMediator, which the real host
        // supplies via AddMediator in Program.cs. A stub is enough here: these tests
        // build options, they never save changes, so no event is ever dispatched.
        services.AddSingleton(Mock.Of<IMediator>());

        return services;
    }

    private static ServiceProvider BuildContainer(string? provider, string connectionString)
    {
        var services = BuildServices();
        services.AddInfrastructure(BuildConfiguration(provider, connectionString));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs <paramref name="inspect"/> against the registered MasterDbContext options
    /// while the container is still alive, then tears everything down.
    /// </summary>
    private static T WithMasterOptions<T>(
        string? provider,
        string connectionString,
        Func<DbContextOptions<MasterDbContext>, T> inspect)
    {
        using var sp = BuildContainer(provider, connectionString);
        using var scope = sp.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<MasterDbContext>>();
        return inspect(options);
    }

    private static string ProviderNameOf(DbContextOptions<MasterDbContext> options)
    {
        using var context = new MasterDbContext(options);
        return context.Database.ProviderName!;
    }

    private static RelationalOptionsExtension RelationalExtension(
        DbContextOptions<MasterDbContext> options) =>
        options.Extensions.OfType<RelationalOptionsExtension>().Single();

    // Returned as object[] rather than IEnumerable<IInterceptor>: Moq pulls in a
    // Castle.DynamicProxy IInterceptor that collides with EF Core's, and the assertions
    // below only need the instances, not the interface.
    private static object[] InterceptorsOf(DbContextOptions<MasterDbContext> options) =>
        options.FindExtension<CoreOptionsExtension>()?.Interceptors?.Cast<object>().ToArray() ?? [];

    // ── 1 & 2. Provider selection follows Database:Provider ───────────────────

    [Fact]
    public void SqlServer_SelectsSqlServerProvider()
    {
        var providerName = WithMasterOptions("SqlServer", SqlServerConnectionString, ProviderNameOf);

        Assert.Equal(SqlServerProviderName, providerName);
    }

    [Fact]
    public void PostgreSql_SelectsNpgsqlProvider()
    {
        var providerName = WithMasterOptions("PostgreSql", PostgreSqlConnectionString, ProviderNameOf);

        Assert.Equal(PostgreSqlProviderName, providerName);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("SQLSERVER")]
    public void SqlServer_ProviderSelectionIsCaseInsensitive(string configuredValue)
    {
        var providerName = WithMasterOptions(configuredValue, SqlServerConnectionString, ProviderNameOf);

        Assert.Equal(SqlServerProviderName, providerName);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    public void PostgreSql_ProviderSelectionIsCaseInsensitive(string configuredValue)
    {
        var providerName = WithMasterOptions(configuredValue, PostgreSqlConnectionString, ProviderNameOf);

        Assert.Equal(PostgreSqlProviderName, providerName);
    }

    // ── 3 & 4. Correct migrations assembly per provider ──────────────────────

    [Fact]
    public void SqlServer_UsesSqlServerMigrationsAssembly()
    {
        var migrationsAssembly = WithMasterOptions(
            "SqlServer", SqlServerConnectionString, o => RelationalExtension(o).MigrationsAssembly);

        Assert.NotNull(migrationsAssembly);
        Assert.StartsWith("ERP.Persistence.SqlServer", migrationsAssembly);
        Assert.DoesNotContain("PostgreSql", migrationsAssembly);
    }

    [Fact]
    public void PostgreSql_UsesPostgreSqlMigrationsAssembly()
    {
        var migrationsAssembly = WithMasterOptions(
            "PostgreSql", PostgreSqlConnectionString, o => RelationalExtension(o).MigrationsAssembly);

        Assert.NotNull(migrationsAssembly);
        Assert.StartsWith("ERP.Persistence.PostgreSql", migrationsAssembly);
        Assert.DoesNotContain("SqlServer", migrationsAssembly);
    }

    // The seams also own the retry policy and command timeout. Asserting the timeout
    // survives proves registration really delegates to the seam rather than
    // re-implementing provider configuration inline.
    [Theory]
    [InlineData("SqlServer", SqlServerConnectionString)]
    [InlineData("PostgreSql", PostgreSqlConnectionString)]
    public void ProviderSeam_ConfigurationIsApplied(string provider, string connectionString)
    {
        var commandTimeout = WithMasterOptions(
            provider, connectionString, o => RelationalExtension(o).CommandTimeout);

        Assert.Equal(30, commandTimeout);
    }

    // ── 5 & 6. Fail closed ───────────────────────────────────────────────────

    // Registration resolves the provider eagerly, so an absent key must throw while
    // AddInfrastructure runs -- at startup -- not lazily on first DbContext resolution.
    [Fact]
    public void MissingProvider_FailsClosedDuringRegistration()
    {
        var services = BuildServices();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddInfrastructure(BuildConfiguration(null, SqlServerConnectionString)));

        Assert.Contains("Database:Provider", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankProvider_FailsClosedDuringRegistration(string configuredValue)
    {
        var services = BuildServices();

        Assert.Throws<InvalidOperationException>(
            () => services.AddInfrastructure(BuildConfiguration(configuredValue, SqlServerConnectionString)));
    }

    [Theory]
    [InlineData("Oracle")]
    [InlineData("MySql")]
    [InlineData("Postgres")]
    [InlineData("nonsense")]
    public void UnsupportedProvider_FailsClosedDuringRegistration(string configuredValue)
    {
        var services = BuildServices();

        var exception = Assert.Throws<InvalidOperationException>(
            () => services.AddInfrastructure(BuildConfiguration(configuredValue, SqlServerConnectionString)));

        Assert.Contains("Database:Provider", exception.Message);
    }

    // No invalid value may yield a working SQL Server registration -- the specific
    // regression that would make a PostgreSQL deployment silently run on SQL Server.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Oracle")]
    public void InvalidProvider_NeverProducesAWorkingRegistration(string? configuredValue)
    {
        var services = BuildServices();

        var exception = Record.Exception(
            () => services.AddInfrastructure(BuildConfiguration(configuredValue, SqlServerConnectionString)));

        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    // ── 7 & 8. CRITICAL REGRESSION GUARD: both interceptors survive ──────────

    [Theory]
    [InlineData("SqlServer", SqlServerConnectionString)]
    [InlineData("PostgreSql", PostgreSqlConnectionString)]
    public void BothInterceptors_AreAttachedUnderEitherProvider(string provider, string connectionString)
    {
        var types = WithMasterOptions(
            provider, connectionString, o => InterceptorsOf(o).Select(i => i.GetType()).ToArray());

        Assert.Contains(typeof(AuditableEntityInterceptor), types);
        Assert.Contains(typeof(DomainEventDispatcherInterceptor), types);
    }

    [Theory]
    [InlineData("SqlServer", SqlServerConnectionString)]
    [InlineData("PostgreSql", PostgreSqlConnectionString)]
    public void AuditableEntityInterceptor_IsAttachedExactlyOnce(string provider, string connectionString)
    {
        var count = WithMasterOptions(
            provider, connectionString, o => InterceptorsOf(o).OfType<AuditableEntityInterceptor>().Count());

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("SqlServer", SqlServerConnectionString)]
    [InlineData("PostgreSql", PostgreSqlConnectionString)]
    public void DomainEventDispatcherInterceptor_IsAttachedExactlyOnce(string provider, string connectionString)
    {
        var count = WithMasterOptions(
            provider, connectionString, o => InterceptorsOf(o).OfType<DomainEventDispatcherInterceptor>().Count());

        Assert.Equal(1, count);
    }

    // Both interceptors must also still be resolvable from the container, since
    // registration resolves them via GetRequiredService.
    [Theory]
    [InlineData("SqlServer", SqlServerConnectionString)]
    [InlineData("PostgreSql", PostgreSqlConnectionString)]
    public void BothInterceptors_RemainResolvableFromTheContainer(string provider, string connectionString)
    {
        using var sp = BuildContainer(provider, connectionString);
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AuditableEntityInterceptor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DomainEventDispatcherInterceptor>());
    }

    // ── MasterDbContext is still usable as a MasterDbContext ─────────────────

    [Theory]
    [InlineData("SqlServer", SqlServerConnectionString)]
    [InlineData("PostgreSql", PostgreSqlConnectionString)]
    public void MasterDbContext_ResolvesFromTheContainerUnderEitherProvider(
        string provider, string connectionString)
    {
        using var sp = BuildContainer(provider, connectionString);
        using var scope = sp.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

        Assert.NotNull(context);
    }
}
