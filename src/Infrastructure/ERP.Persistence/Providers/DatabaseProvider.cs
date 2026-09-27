namespace ERP.Persistence.Providers;

/// <summary>
/// DATABASE PROVIDER — The database engine a deployment runs on.
///
/// Saty ERP supports SQL Server and PostgreSQL as first-class providers, chosen per
/// customer deployment. Within one deployment the Master/Identity database and every
/// tenant database use the same engine; mixing engines inside one deployment is not a
/// supported requirement.
///
/// This enum deliberately lives in ERP.Persistence and carries NO provider packages,
/// NO IConfiguration and NO ASP.NET Core dependency, so both the runtime composition
/// root and the design-time EF factories can share one semantic definition without
/// either taking a dependency on the other.
/// </summary>
public enum DatabaseProvider
{
    /// <summary>Microsoft SQL Server.</summary>
    SqlServer = 1,

    /// <summary>PostgreSQL.</summary>
    PostgreSql = 2,
}

/// <summary>
/// DATABASE PROVIDER RESOLUTION — Parses the configured provider name, and FAILS CLOSED.
///
/// WHY FAIL CLOSED? (This is the whole point of this type.)
/// =========================================================
/// A deployment's database engine is not something to guess at. If the setting is
/// absent, blank or misspelled, silently falling back to SQL Server would let a
/// PostgreSQL deployment start up and quietly connect with the wrong provider — the
/// failure would surface later, as data or migration corruption, far from its cause.
///
/// So: anything other than an exact (case-insensitive) match on a supported name
/// throws. There is no default. The value in appsettings.json is an explicit
/// configured choice, not a fallback.
///
/// USAGE: each consumer reads the raw string from its own configuration source and
/// passes it here. This type never reads configuration itself — that is what keeps it
/// free of an IConfiguration dependency and trivially testable.
/// </summary>
public static class DatabaseProviderResolver
{
    /// <summary>
    /// Configuration key holding the provider name — <c>Database:Provider</c>.
    /// Referenced in error messages so a misconfiguration names the key to fix.
    /// </summary>
    public const string ConfigurationKey = "Database:Provider";

    /// <summary>Supported names, in the casing used by configuration and documentation.</summary>
    public const string SupportedProviders = "SqlServer, PostgreSql";

    /// <summary>
    /// Parses a configured provider name into <see cref="DatabaseProvider"/>.
    /// Matching is case-insensitive; surrounding whitespace is tolerated.
    /// </summary>
    /// <param name="configuredValue">
    /// The raw value of <c>Database:Provider</c>. May be null when the key is absent.
    /// </param>
    /// <returns>The resolved provider.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the value is null, empty, whitespace, or not a supported provider name.
    /// Never returns a default.
    /// </exception>
    public static DatabaseProvider Parse(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            throw new InvalidOperationException(
                $"Database provider is missing. " +
                $"Set {ConfigurationKey} in appsettings.json or environment variables. " +
                $"Supported providers: {SupportedProviders}");
        }

        var trimmed = configuredValue.Trim();

        if (string.Equals(trimmed, nameof(DatabaseProvider.SqlServer), StringComparison.OrdinalIgnoreCase))
            return DatabaseProvider.SqlServer;

        if (string.Equals(trimmed, nameof(DatabaseProvider.PostgreSql), StringComparison.OrdinalIgnoreCase))
            return DatabaseProvider.PostgreSql;

        throw new InvalidOperationException(
            $"Unsupported database provider: '{trimmed}'. " +
            $"Set {ConfigurationKey} to one of the supported providers: {SupportedProviders}");
    }
}
