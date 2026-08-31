using Microsoft.Extensions.Logging;
using SocietyHub.Caching;
using SocietyHub.SharedKernel.Features;
using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Features;

/// <summary>
/// Where entitlements come from.
///
/// One service owns the write side — Society, which owns the society aggregate — and every
/// other service reads. Splitting the interface this way keeps the read path identical
/// everywhere while letting the owning service back it with its own tables and everyone else
/// back it with the shared cache.
/// </summary>
public interface IEntitlementSource
{
    Task<SocietyEntitlements?> GetAsync(Guid societyId, CancellationToken cancellationToken = default);

    Task<FeatureRolloutMap> GetRolloutsAsync(CancellationToken cancellationToken = default);
}

public sealed class FeatureGateOptions
{
    public const string SectionName = "Features";

    /// <summary>
    /// How long an entitlement snapshot is trusted.
    ///
    /// Ten minutes is a deliberate compromise. Entitlements change a few times a month, so a
    /// long TTL costs almost nothing in staleness — but the emergency case is switching a
    /// misbehaving feature off, and waiting an hour for that is not acceptable. Writes also
    /// evict the key directly, so this only bounds the damage when an eviction is missed.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Rollout waves change less often and apply platform-wide.</summary>
    public TimeSpan RolloutCacheDuration { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// The feature gate every service uses.
///
/// Runs on nearly every request, so the hot path is a Redis read of a single small object and
/// a set lookup. The interesting behaviour is what happens when that read fails — see
/// <see cref="ResolveAsync"/>.
/// </summary>
public sealed class CachedFeatureGate : IFeatureGate
{
    private readonly ICacheStore _cache;
    private readonly IEntitlementSource _source;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _timeProvider;
    private readonly FeatureGateOptions _options;
    private readonly ILogger<CachedFeatureGate> _logger;

    public CachedFeatureGate(
        ICacheStore cache,
        IEntitlementSource source,
        ITenantContext tenant,
        TimeProvider timeProvider,
        FeatureGateOptions options,
        ILogger<CachedFeatureGate> logger)
    {
        _cache = cache;
        _source = source;
        _tenant = tenant;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public Task<bool> IsEnabledAsync(
        string featureKey, CancellationToken cancellationToken = default) =>
        IsEnabledForAsync(_tenant.RequireSocietyId(), featureKey, cancellationToken);

    public async Task<bool> IsEnabledForAsync(
        Guid societyId, string featureKey, CancellationToken cancellationToken = default)
    {
        var enabled = await ResolveAsync(societyId, cancellationToken);
        return enabled.Contains(featureKey);
    }

    public Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(
        CancellationToken cancellationToken = default) =>
        ResolveAsync(_tenant.RequireSocietyId(), cancellationToken);

    private async Task<IReadOnlySet<string>> ResolveAsync(
        Guid societyId, CancellationToken cancellationToken)
    {
        var key = CacheKey.ForSociety(societyId, "entitlements");

        SocietyEntitlements entitlements;
        FeatureRolloutMap rollouts;

        try
        {
            entitlements = await _cache.GetOrCreateAsync(
                key,
                async ct => await _source.GetAsync(societyId, ct)
                            ?? SocietyEntitlements.Fallback(societyId),
                _options.CacheDuration,
                cancellationToken);

            rollouts = await _source.GetRolloutsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Both the cache and its backing source are unreachable. A guard mid-shift must
            // not find the gate switched off because Redis restarted — and a Basic society
            // must not be handed the camera features it never paid for. The baseline is
            // exactly the set that satisfies both, so it is what we fall back to.
            _logger.LogError(
                ex,
                "Entitlements for society {SocietyId} are unavailable. "
                + "Falling back to the baseline feature set.",
                societyId);

            return PlanCatalogue.Baseline;
        }

        return entitlements.Resolve(_timeProvider.GetUtcNow(), rollouts);
    }
}

/// <summary>
/// The read-side source used by every service except Society.
///
/// Reads the snapshot the Society service publishes into the shared cache. There is no
/// database fallback on purpose: a Gate service that could query Society's tables would be a
/// second write path into another service's schema, and the first migration there would break
/// four services at once. A cold cache degrades to the baseline instead, which is a bounded
/// and understood failure rather than a coupling that lasts forever.
/// </summary>
public sealed class CachedEntitlementSource : IEntitlementSource
{
    private readonly ICacheStore _cache;

    public CachedEntitlementSource(ICacheStore cache) => _cache = cache;

    public Task<SocietyEntitlements?> GetAsync(
        Guid societyId, CancellationToken cancellationToken = default) =>
        _cache.GetAsync<SocietyEntitlements>(
            CacheKey.ForSociety(societyId, "entitlements", "snapshot"), cancellationToken);

    public async Task<FeatureRolloutMap> GetRolloutsAsync(
        CancellationToken cancellationToken = default)
    {
        var rollouts = await _cache.GetAsync<List<FeatureRollout>>(
            CacheKey.ForPlatformWideData("rollouts"), cancellationToken);

        return rollouts is null ? FeatureRolloutMap.Empty : new FeatureRolloutMap(rollouts);
    }
}
