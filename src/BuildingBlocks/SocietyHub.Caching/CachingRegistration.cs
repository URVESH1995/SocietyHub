using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SocietyHub.Caching;

public static class CachingRegistration
{
    /// <summary>
    /// Registers the cache and the distributed lock.
    ///
    /// Expects <c>IConnectionMultiplexer</c> to already be registered — Aspire's
    /// <c>AddRedisClient</c> does that, and sharing one multiplexer is deliberate: it is
    /// designed to be a long-lived singleton, and creating one per component is the classic
    /// way to exhaust connections under load.
    /// </summary>
    public static IServiceCollection AddSocietyHubCaching(this IServiceCollection services)
    {
        services.TryAddSingleton<ICacheStore, RedisCacheStore>();
        services.TryAddSingleton<IDistributedLock, RedisDistributedLock>();

        return services;
    }
}
