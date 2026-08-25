using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace SocietyHub.Caching;

/// <summary>A held lock. Disposing releases it.</summary>
public interface ILockHandle : IAsyncDisposable
{
    string Resource { get; }

    /// <summary>Extends the lease. Returns false if the lock was already lost.</summary>
    Task<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default);
}

public interface IDistributedLock
{
    /// <summary>
    /// Tries to take <paramref name="resource"/>. Returns <see langword="null"/> immediately
    /// if someone else holds it — callers decide whether to wait, skip or fail.
    /// </summary>
    Task<ILockHandle?> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single-instance Redis lock.
///
/// Its intended use is the Phase 2 bulk-drive quorum: two residents enrolling at the same
/// instant must not both read "24 joined" and both write "25", which would cross a slab
/// threshold once instead of twice and price the drive wrong for everybody.
///
/// The honest limitation: this is a lease, not a consensus algorithm. If the Redis primary
/// fails over before replicating the key, two holders are possible. That is acceptable here
/// because the protected operations are also guarded by a database constraint or an
/// idempotency key — the lock reduces contention, it is not the last line of correctness.
/// Anything where a double-execution is unrecoverable must not rely on this alone.
/// </summary>
public sealed class RedisDistributedLock : IDistributedLock
{
    /// <summary>
    /// Release compares the token before deleting.
    ///
    /// Without this check, a caller whose lease expired mid-work would delete the lock a
    /// <em>different</em> caller now legitimately holds — quietly turning mutual exclusion off
    /// at exactly the moment contention is highest. Lua makes the compare-and-delete atomic.
    /// </summary>
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        else
            return 0
        end
        """;

    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        else
            return 0
        end
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDistributedLock> _logger;

    public RedisDistributedLock(IConnectionMultiplexer redis, ILogger<RedisDistributedLock> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<ILockHandle?> TryAcquireAsync(
        string resource,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl), "A lock lease must expire, or a crashed holder blocks it forever.");
        }

        var key = $"sh:lock:{resource}";

        // Unique per acquisition, so only this holder can release this lease.
        var token = Guid.CreateVersion7().ToString("N");

        try
        {
            var acquired = await _redis.GetDatabase()
                .StringSetAsync(key, token, ttl, When.NotExists);

            return acquired ? new RedisLockHandle(_redis, key, token, resource, _logger) : null;
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
        {
            // Unlike the cache, a lock cannot fail open. Reporting "not acquired" makes the
            // caller skip or retry; pretending success would let two holders run concurrently.
            _logger.LogError(ex, "Could not reach Redis to acquire lock {Resource}.", resource);
            return null;
        }
    }

    private sealed class RedisLockHandle : ILockHandle
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly string _key;
        private readonly string _token;
        private readonly ILogger _logger;
        private bool _released;

        public RedisLockHandle(
            IConnectionMultiplexer redis,
            string key,
            string token,
            string resource,
            ILogger logger)
        {
            _redis = redis;
            _key = key;
            _token = token;
            Resource = resource;
            _logger = logger;
        }

        public string Resource { get; }

        public async Task<bool> RenewAsync(TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            if (_released)
            {
                return false;
            }

            var result = await _redis.GetDatabase().ScriptEvaluateAsync(
                RenewScript,
                [_key],
                [_token, (long)ttl.TotalMilliseconds]);

            return (int)result == 1;
        }

        public async ValueTask DisposeAsync()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            try
            {
                await _redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [_key], [_token]);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                // Not fatal. The lease expires on its own, so the worst outcome is other
                // callers waiting out the remaining TTL rather than a permanently held lock.
                _logger.LogWarning(ex, "Could not release lock {Resource}; it will expire.", Resource);
            }
        }
    }
}
