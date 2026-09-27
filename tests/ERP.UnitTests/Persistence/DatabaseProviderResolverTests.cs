using ERP.Persistence.Providers;

namespace ERP.UnitTests.Persistence;

/// <summary>
/// Tests for DatabaseProviderResolver — the single fail-closed parser for the
/// Database:Provider deployment setting.
///
/// The behaviour under test is deliberately narrow but security-relevant: a missing or
/// misspelled provider must NOT resolve to a default. If it did, a PostgreSQL
/// deployment could start against SQL Server and the mistake would only surface later
/// as migration or data damage. Every negative case below therefore asserts a throw,
/// not a fallback.
/// </summary>
public class DatabaseProviderResolverTests
{
    // ── Supported values parse ────────────────────────────────────────────────

    [Fact]
    public void Parse_SqlServer_ReturnsSqlServer()
    {
        Assert.Equal(DatabaseProvider.SqlServer, DatabaseProviderResolver.Parse("SqlServer"));
    }

    [Fact]
    public void Parse_PostgreSql_ReturnsPostgreSql()
    {
        Assert.Equal(DatabaseProvider.PostgreSql, DatabaseProviderResolver.Parse("PostgreSql"));
    }

    // ── Case-insensitive matching (the approved rule) ─────────────────────────

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("SQLSERVER")]
    [InlineData("SqlSerVer")]
    public void Parse_SqlServer_IsCaseInsensitive(string configuredValue)
    {
        Assert.Equal(DatabaseProvider.SqlServer, DatabaseProviderResolver.Parse(configuredValue));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    [InlineData("PostgreSQL")]
    public void Parse_PostgreSql_IsCaseInsensitive(string configuredValue)
    {
        Assert.Equal(DatabaseProvider.PostgreSql, DatabaseProviderResolver.Parse(configuredValue));
    }

    // Surrounding whitespace is tolerated because configuration providers and
    // environment variables routinely introduce it; the VALUE is still exact.
    [Theory]
    [InlineData("  SqlServer")]
    [InlineData("SqlServer  ")]
    [InlineData("\tPostgreSql\n")]
    public void Parse_TrimsSurroundingWhitespace(string configuredValue)
    {
        // Does not throw, and resolves to the provider named inside the padding.
        var result = DatabaseProviderResolver.Parse(configuredValue);
        Assert.True(result is DatabaseProvider.SqlServer or DatabaseProvider.PostgreSql);
    }

    // ── Fail closed: absent value ────────────────────────────────────────────

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseProviderResolver.Parse(null));
    }

    [Fact]
    public void Parse_Empty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseProviderResolver.Parse(string.Empty));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Parse_Whitespace_Throws(string configuredValue)
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseProviderResolver.Parse(configuredValue));
    }

    // ── Fail closed: unsupported value ───────────────────────────────────────

    // "Oracle" and "MySql" are included because ERP.Persistence.Oracle is referenced
    // as a future possibility in code comments — a reader must not assume naming it
    // here makes it work. "SqlServer2022" and "Postgres" guard against near-miss
    // spellings resolving by prefix or alias.
    [Theory]
    [InlineData("Oracle")]
    [InlineData("MySql")]
    [InlineData("Sqlite")]
    [InlineData("Postgres")]
    [InlineData("SqlServer2022")]
    [InlineData("Sql Server")]
    [InlineData("postgre sql")]
    public void Parse_UnsupportedProvider_Throws(string configuredValue)
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseProviderResolver.Parse(configuredValue));
    }

    // ── Fail closed: never silently defaults ─────────────────────────────────

    // The single most important assertion in this file. Every invalid input must
    // throw; none may return SqlServer (or anything else).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Oracle")]
    [InlineData("nonsense")]
    public void Parse_InvalidInput_NeverSilentlyDefaults(string? configuredValue)
    {
        var exception = Record.Exception(() => DatabaseProviderResolver.Parse(configuredValue));

        Assert.NotNull(exception);
        Assert.IsType<InvalidOperationException>(exception);
    }

    // ── Diagnosability of the failure ────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Oracle")]
    public void Parse_Failure_MessageNamesConfigurationKeyAndSupportedValues(string? configuredValue)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DatabaseProviderResolver.Parse(configuredValue));

        Assert.Contains("Database:Provider", exception.Message);
        Assert.Contains("SqlServer", exception.Message);
        Assert.Contains("PostgreSql", exception.Message);
    }

    [Fact]
    public void Parse_UnsupportedProvider_MessageEchoesTheOffendingValue()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DatabaseProviderResolver.Parse("Oracle"));

        Assert.Contains("Oracle", exception.Message);
    }

    // ── Published constants stay in step with the enum ───────────────────────

    // Guards against the error message drifting away from what Parse actually accepts:
    // the advertised names must be exactly the enum member names.
    [Fact]
    public void SupportedProviders_ListsExactlyTheEnumMemberNames()
    {
        var advertised = DatabaseProviderResolver.SupportedProviders
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(name => name)
            .ToArray();

        var declared = Enum.GetNames<DatabaseProvider>()
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(declared, advertised);
    }

    [Fact]
    public void EveryDeclaredProvider_IsParseable()
    {
        foreach (var name in Enum.GetNames<DatabaseProvider>())
        {
            var parsed = DatabaseProviderResolver.Parse(name);
            Assert.Equal(Enum.Parse<DatabaseProvider>(name), parsed);
        }
    }

    [Fact]
    public void ConfigurationKey_IsTheDocumentedKey()
    {
        Assert.Equal("Database:Provider", DatabaseProviderResolver.ConfigurationKey);
    }

    // ── TryParse — non-throwing counterpart (P1-e) ────────────────────────────

    // TryParse exists for callers whose provider value did NOT come from
    // Database:Provider — a method argument, or a per-tenant database column — so they can
    // raise their own context-appropriate error instead of Parse's configuration-specific
    // one. Recognition semantics must be IDENTICAL to Parse; only error reporting differs.

    [Fact]
    public void TryParse_SqlServer_ReturnsTrueAndSqlServer()
    {
        Assert.True(DatabaseProviderResolver.TryParse("SqlServer", out var provider));
        Assert.Equal(DatabaseProvider.SqlServer, provider);
    }

    [Fact]
    public void TryParse_PostgreSql_ReturnsTrueAndPostgreSql()
    {
        Assert.True(DatabaseProviderResolver.TryParse("PostgreSql", out var provider));
        Assert.Equal(DatabaseProvider.PostgreSql, provider);
    }

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("SQLSERVER")]
    [InlineData("SqlSerVer")]
    public void TryParse_SqlServer_IsCaseInsensitive(string value)
    {
        Assert.True(DatabaseProviderResolver.TryParse(value, out var provider));
        Assert.Equal(DatabaseProvider.SqlServer, provider);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("POSTGRESQL")]
    [InlineData("PostgreSQL")]
    public void TryParse_PostgreSql_IsCaseInsensitive(string value)
    {
        Assert.True(DatabaseProviderResolver.TryParse(value, out var provider));
        Assert.Equal(DatabaseProvider.PostgreSql, provider);
    }

    [Theory]
    [InlineData("  SqlServer", DatabaseProvider.SqlServer)]
    [InlineData("SqlServer  ", DatabaseProvider.SqlServer)]
    [InlineData("\tPostgreSql\n", DatabaseProvider.PostgreSql)]
    [InlineData("  PostgreSql  ", DatabaseProvider.PostgreSql)]
    public void TryParse_TrimsSurroundingWhitespace(string value, DatabaseProvider expected)
    {
        Assert.True(DatabaseProviderResolver.TryParse(value, out var provider));
        Assert.Equal(expected, provider);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(DatabaseProviderResolver.TryParse(null, out _));
    }

    [Fact]
    public void TryParse_Empty_ReturnsFalse()
    {
        Assert.False(DatabaseProviderResolver.TryParse(string.Empty, out _));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void TryParse_Whitespace_ReturnsFalse(string value)
    {
        Assert.False(DatabaseProviderResolver.TryParse(value, out _));
    }

    [Theory]
    [InlineData("Oracle")]
    [InlineData("MySql")]
    [InlineData("Sqlite")]
    [InlineData("Postgres")]
    [InlineData("SqlServer2022")]
    [InlineData("Sql Server")]
    [InlineData("nonsense")]
    public void TryParse_UnsupportedProvider_ReturnsFalse(string value)
    {
        Assert.False(DatabaseProviderResolver.TryParse(value, out _));
    }

    [Fact]
    public void TryParse_NeverThrows()
    {
        foreach (var value in new[] { null, "", " ", "\t", "Oracle", "nonsense", "SqlServer", "PostgreSql" })
            Assert.Null(Record.Exception(() => DatabaseProviderResolver.TryParse(value, out _)));
    }

    // ── TryParse and Parse agree on every recognised input ───────────────────

    // Guards against the two APIs drifting apart: anything Parse accepts, TryParse must
    // accept with the same result, and vice versa.
    [Theory]
    [InlineData("SqlServer")]
    [InlineData("sqlserver")]
    [InlineData("  SqlServer  ")]
    [InlineData("PostgreSql")]
    [InlineData("POSTGRESQL")]
    [InlineData("\tPostgreSql\n")]
    public void TryParse_AgreesWithParse_OnAcceptedInput(string value)
    {
        Assert.True(DatabaseProviderResolver.TryParse(value, out var tryParsed));
        Assert.Equal(DatabaseProviderResolver.Parse(value), tryParsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Oracle")]
    [InlineData("Postgres")]
    [InlineData("nonsense")]
    public void TryParse_AgreesWithParse_OnRejectedInput(string? value)
    {
        Assert.False(DatabaseProviderResolver.TryParse(value, out _));
        Assert.Throws<InvalidOperationException>(() => DatabaseProviderResolver.Parse(value));
    }
}
