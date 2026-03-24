namespace ERP.Application.Abstractions.Caching;

/// <summary>
/// CACHE SERVICE INTERFACE — Abstracts caching so business logic doesn't know
/// whether we're using Redis, in-memory, or any other cache provider.
///
/// WHAT IS CACHING? (Interview Question!)
/// =======================================
/// Caching stores frequently accessed data in fast memory (RAM) so we don't
/// hit the database every time. Think of it like a "shortcut" to data.
///
/// WITHOUT CACHE:
///   Request → Controller → Handler → Database (100ms)
///   Next request → Controller → Handler → Database (100ms) again!
///
/// WITH CACHE:
///   First request  → Controller → Handler → Database (100ms) → Store in cache
///   Next request   → Controller → Handler → Cache (1ms) → Return instantly!
///
/// CACHE STRATEGIES (L1/L2):
/// ==========================
/// L1 (In-Memory):  Lives in the app's memory. Fastest (microseconds).
///                   Lost when app restarts. Not shared across instances.
///                   Use for: config, lookup tables, small reference data.
///
/// L2 (Distributed/Redis): Lives on a separate Redis server. Fast (milliseconds).
///                          Survives app restarts. Shared across all instances.
///                          Use for: session data, token blacklists, heavy queries.
///
/// DESIGN PATTERN: Strategy Pattern (swap cache implementations without changing code)
/// </summary>
public interface ICacheService
{
    /// <summary>Get a cached value by key. Returns null if not found or expired.</summary>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);

    /// <summary>Store a value in cache with an expiration time.</summary>
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken ct = default);

    /// <summary>Remove a cached value by key.</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>Remove all cached values matching a prefix (e.g., "jobcard:*").</summary>
    Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// Get-or-create pattern: if the key exists in cache, return it.
    /// If not, call the factory function, cache the result, and return it.
    ///
    /// EXAMPLE:
    /// var departments = await _cache.GetOrCreateAsync(
    ///     "departments:all",
    ///     () => _repo.GetAllAsync(),
    ///     TimeSpan.FromMinutes(30));
    /// </summary>
    Task<T?> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiration = null, CancellationToken ct = default);
}
