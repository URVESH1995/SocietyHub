using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace SocietyHub.Caching;

public interface ICacheStore
{
    Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(CacheKey key, T value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Returns the cached value, or produces and caches it.</summary>
    Task<T> GetOrCreateAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(CacheKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops everything cached for one society. Used when a committee changes settings that
    /// half a dozen cached shapes depend on, and on offboarding.
    /// </summary>
    Task RemoveSocietyAsync(Guid societyId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICacheStore" />
public sealed class RedisCacheStore : ICacheStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisCacheStore> _logger;

    public RedisCacheStore(IConnectionMultiplexer redis, ILogger<RedisCacheStore> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(key.Value);

            return value.IsNullOrEmpty
                ? default
                : JsonSerializer.Deserialize<T>((string)value!, SerializerOptions);
        }
        catch (Exception ex) when (IsCacheFault(ex))
        {
            // A cache is an optimisation, never a source of truth. Redis being unreachable
            // must degrade the system to "slower", not to "down" — so a read failure reports
            // a miss and the caller goes to the database.
            _logger.LogWarning(ex, "Cache read failed for {Key}; treating as a miss.", key.Value);
            return default;
        }
    }

    public async Task SetAsync<T>(
        CacheKey key,
        T value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(value, SerializerOptions);
            await _redis.GetDatabase().StringSetAsync(key.Value, payload, ttl);
        }
        catch (Exception ex) when (IsCacheFault(ex))
        {
            _logger.LogWarning(ex, "Cache write failed for {Key}; continuing uncached.", key.Value);
        }
    }

    public async Task<T> GetOrCreateAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var cached = await GetAsync<T>(key, cancellationToken);

        if (cached is not null)
        {
            return cached;
        }

        // No stampede protection here on purpose. Several callers may produce the same value
        // concurrently on a cold key, which for society profile data is a handful of extra
        // reads. Serialising them behind a lock would add a Redis round trip to every miss and
        // a failure mode — a held lock stalling every reader — worse than the duplication.
        var produced = await factory(cancellationToken);

        if (produced is not null)
        {
            await SetAsync(key, produced, ttl, cancellationToken);
        }

        return produced;
    }

    public async Task RemoveAsync(CacheKey key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase().KeyDeleteAsync(key.Value);
        }
        catch (Exception ex) when (IsCacheFault(ex))
        {
            // Worth a louder log than a read failure: a stale entry that should have been
            // invalidated will now serve wrong data until its TTL expires.
            _logger.LogError(ex, "Cache invalidation FAILED for {Key}; stale until TTL.", key.Value);
        }
    }

    public async Task RemoveSocietyAsync(Guid societyId, CancellationToken cancellationToken = default)
    {
        var prefix = CacheKey.SocietyPrefix(societyId);

        try
        {
            // SCAN rather than KEYS, which blocks the server for the whole sweep. Endpoints
            // are enumerated because a replica holds a different keyspace slice.
            foreach (var endpoint in _redis.GetEndPoints())
            {
                var server = _redis.GetServer(endpoint);

                if (server.IsReplica)
                {
                    continue;
                }

                await foreach (var key in server
                    .KeysAsync(pattern: prefix + "*", pageSize: 250)
                    .WithCancellation(cancellationToken))
                {
                    await _redis.GetDatabase().KeyDeleteAsync(key);
                }
            }
        }
        catch (Exception ex) when (IsCacheFault(ex))
        {
            _logger.LogError(
                ex, "Failed to invalidate cache for society {SocietyId}; stale until TTL.", societyId);
        }
    }

    /// <summary>
    /// Faults worth swallowing. A serialisation error is a coding defect and must surface, not
    /// be silently degraded into a cache miss that hides it forever.
    /// </summary>
    private static bool IsCacheFault(Exception ex) =>
        ex is RedisException or RedisTimeoutException or TimeoutException
            or ObjectDisposedException;
}
