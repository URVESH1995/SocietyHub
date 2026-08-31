using System.Security.Cryptography;
using System.Text;

namespace SocietyHub.Features;

public enum RolloutStage
{
    /// <summary>Nobody. The state a feature ships in.</summary>
    Off = 0,

    /// <summary>A named list of societies. How every rollout starts.</summary>
    Pilot = 1,

    /// <summary>A deterministic share of all societies.</summary>
    Percentage = 2,

    /// <summary>Everyone. The feature has graduated and the rollout row can be retired.</summary>
    On = 3,
}

/// <summary>
/// How far a feature has been rolled out.
///
/// The platform ships new features every year to ~170 societies that did not ask for them and
/// cannot all be watched at once. A release that flips a feature on everywhere is a release
/// whose first bug report arrives from 170 places simultaneously, which is indistinguishable
/// from an outage. So a feature walks: five societies by name, then a percentage, then all.
/// </summary>
public sealed record FeatureRollout
{
    public required string FeatureKey { get; init; }

    public required RolloutStage Stage { get; init; }

    /// <summary>Societies in the pilot wave, used when <see cref="Stage"/> is Pilot.</summary>
    public IReadOnlyList<Guid> PilotSocietyIds { get; init; } = [];

    /// <summary>0–100, used when <see cref="Stage"/> is Percentage.</summary>
    public int Percentage { get; init; }

    /// <summary>
    /// Whether this rollout reaches a given society.
    ///
    /// The percentage bucket is a hash of the society id and the feature key, not a random
    /// draw. Two properties follow, and both matter more than they look:
    ///
    /// A society lands in the same bucket on every request, so a feature does not flicker on
    /// and off between two page loads — which would produce bug reports nobody can reproduce.
    ///
    /// The key is part of the hash, so the 10% wave of one feature is a different ten percent
    /// from the next. Hashing the society alone would send every canary to the same unlucky
    /// seventeen societies, who would come to experience the platform as permanently broken.
    /// </summary>
    public bool Includes(Guid societyId) => Stage switch
    {
        RolloutStage.On => true,
        RolloutStage.Off => false,
        RolloutStage.Pilot => PilotSocietyIds.Contains(societyId),
        RolloutStage.Percentage => Percentage > 0 && BucketOf(societyId, FeatureKey) < Percentage,
        _ => false,
    };

    /// <summary>Stable bucket in 0–99 for a society and feature.</summary>
    internal static int BucketOf(Guid societyId, string featureKey)
    {
        var bytes = Encoding.UTF8.GetBytes($"{societyId:N}:{featureKey}");

        // SHA-256 rather than string.GetHashCode(), which is randomised per process in .NET.
        // A per-process hash would put a society in a different bucket on every server, so a
        // 10% rollout would reach a different 10% behind each replica.
        var hash = SHA256.HashData(bytes);

        return (hash[0] << 8 | hash[1]) % 100;
    }
}

/// <summary>
/// Every active rollout, resolved together.
///
/// Platform-wide rather than per-society, so it is fetched once and cached once instead of
/// being duplicated into every society's entitlement snapshot.
/// </summary>
public sealed class FeatureRolloutMap
{
    private readonly IReadOnlyList<FeatureRollout> _rollouts;

    public FeatureRolloutMap(IEnumerable<FeatureRollout> rollouts) =>
        _rollouts = rollouts.ToList();

    public static FeatureRolloutMap Empty { get; } = new([]);

    public IEnumerable<string> FeaturesFor(Guid societyId) =>
        _rollouts.Where(r => r.Includes(societyId)).Select(r => r.FeatureKey);
}
