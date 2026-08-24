namespace SocietyHub.SharedKernel.Features;

/// <summary>
/// Answers whether a capability is switched on for the society in scope.
///
/// Resolution is always society-first, plan-second: an explicit per-society override beats
/// whatever the subscription plan grants. That ordering is what makes a pilot possible —
/// five societies can run next year's feature while still on this year's plan, and one
/// society can have a misbehaving feature switched off without a deployment or a rollback
/// that touches the other hundred and sixty-nine.
/// </summary>
public interface IFeatureGate
{
    /// <summary>
    /// Whether <paramref name="featureKey"/> is enabled for the current society.
    /// Backed by a Redis cache, so this is safe to call per request.
    /// </summary>
    Task<bool> IsEnabledAsync(string featureKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the feature is enabled for a named society, for background workers and
    /// message consumers that run outside a request and have no ambient tenant.
    /// </summary>
    Task<bool> IsEnabledForAsync(
        Guid societyId,
        string featureKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every feature enabled for the current society.
    ///
    /// Clients fetch this once at startup and hide what is unavailable, so a resident on a
    /// Basic plan never sees a bulk-drive tab that would only refuse them. Server-side
    /// checks stay mandatory regardless — this shapes the UI, it does not enforce anything.
    /// </summary>
    Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when an endpoint is reached for a feature the society does not have. Surfaces as
/// <c>402 Payment Required</c> for a plan limitation, or <c>404</c> where merely revealing
/// that the feature exists would be a leak.
/// </summary>
public sealed class FeatureNotEnabledException : Exception
{
    public FeatureNotEnabledException(string featureKey, Guid? societyId)
        : base($"Feature '{featureKey}' is not enabled for society " +
               $"'{societyId?.ToString() ?? "none"}'.")
    {
        FeatureKey = featureKey;
        SocietyId = societyId;
    }

    public string FeatureKey { get; }

    public Guid? SocietyId { get; }
}
