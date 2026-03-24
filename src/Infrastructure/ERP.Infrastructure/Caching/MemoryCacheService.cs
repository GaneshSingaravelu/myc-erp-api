using System.Collections.Concurrent;
using ERP.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ERP.Infrastructure.Caching;

/// <summary>
/// IN-MEMORY CACHE SERVICE — L1 cache that lives in the application's memory.
///
/// WHAT IS IN-MEMORY CACHE?
/// =========================
/// Data is stored in the app's RAM using a dictionary-like structure.
///
/// PROS:
/// - Fastest possible access (nanoseconds) — no network calls
/// - No external dependencies (no Redis server needed)
/// - Simple to set up and use
///
/// CONS:
/// - Data is lost when the app restarts
/// - Not shared across multiple app instances (if you run 3 copies of the API,
///   each has its own separate cache)
/// - Limited by app's memory
///
/// WHEN TO USE:
/// - Development and single-instance deployments (our case for now)
/// - Small, frequently-read, rarely-changing data (department lists, config settings)
///
/// WHEN TO USE REDIS INSTEAD:
/// - Multiple app instances need to share cache
/// - Cache must survive app restarts
/// - Large data sets that would consume too much app memory
///
/// REGISTERED AS: Singleton (one instance for the entire app lifetime)
///
/// WHAT IS SINGLETON? (Interview Question!)
/// ==========================================
/// DI Lifetimes determine how long an object lives:
///
/// 1. SINGLETON: ONE instance for the ENTIRE application lifetime.
///    Created once, reused forever. Use for: caches, configuration, logging.
///    ⚠ Must be thread-safe (multiple requests access it simultaneously).
///
/// 2. SCOPED: ONE instance per HTTP REQUEST.
///    Created when request starts, disposed when request ends.
///    Use for: DbContext, UnitOfWork, repositories, current user service.
///    ⚠ Cannot inject Scoped into Singleton (shorter-lived into longer-lived).
///
/// 3. TRANSIENT: NEW instance EVERY TIME it's requested.
///    Created fresh each time. Use for: lightweight, stateless services.
///    ⚠ Can be expensive if the service is heavy to create.
///
/// MEMORY TRICK FOR INTERVIEWS:
///   Singleton = "one for all"
///   Scoped    = "one per request"
///   Transient = "one per injection"
///
/// DESIGN PATTERN: Strategy Pattern (implements ICacheService, can be swapped with Redis)
/// </summary>
public class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<MemoryCacheService> _logger;

    // Track all cache keys so we can implement RemoveByPrefix
    // ConcurrentDictionary is thread-safe (important for Singleton!)
    private readonly ConcurrentDictionary<string, bool> _cacheKeys = new();

    private static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(15);

    public MemoryCacheService(IMemoryCache cache, ILogger<MemoryCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var value = _cache.TryGetValue(key, out T? result) ? result : default;
        _logger.LogDebug("Cache {Status}: {Key}", value is not null ? "HIT" : "MISS", key);
        return Task.FromResult(value);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken ct = default)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiration ?? DefaultExpiration
        };

        _cache.Set(key, value, options);
        _cacheKeys.TryAdd(key, true);
        _logger.LogDebug("Cache SET: {Key} (expires in {Expiration})", key, expiration ?? DefaultExpiration);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _cache.Remove(key);
        _cacheKeys.TryRemove(key, out _);
        _logger.LogDebug("Cache REMOVE: {Key}", key);

        return Task.CompletedTask;
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var keysToRemove = _cacheKeys.Keys.Where(k => k.StartsWith(prefix)).ToList();
        foreach (var key in keysToRemove)
        {
            _cache.Remove(key);
            _cacheKeys.TryRemove(key, out _);
        }

        _logger.LogDebug("Cache REMOVE BY PREFIX: {Prefix} ({Count} keys)", prefix, keysToRemove.Count);
        return Task.CompletedTask;
    }

    public async Task<T?> GetOrCreateAsync<T>(
        string key,
        Func<Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken ct = default)
    {
        var existing = await GetAsync<T>(key, ct);
        if (existing is not null)
            return existing;

        var value = await factory();
        await SetAsync(key, value, expiration, ct);
        return value;
    }
}
