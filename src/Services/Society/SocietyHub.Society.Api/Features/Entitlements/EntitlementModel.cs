using SocietyHub.Features;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Society.Api.Features.Entitlements;

/// <summary>
/// A society's subscription and its per-society switches.
///
/// Deliberately not part of the <c>Society</c> aggregate. A society is a place with towers and
/// flats; a subscription is a commercial arrangement that changes on a different clock, is
/// edited by different people, and would otherwise drag billing concerns into every query that
/// only wanted a tower name.
/// </summary>
public sealed class SocietySubscription : Entity
{
    private SocietySubscription() { }

    public SocietySubscription(Guid id, Guid societyId, SubscriptionPlan plan)
        : base(id)
    {
        SocietyId = societyId;
        Plan = plan;
    }

    public Guid SocietyId { get; private set; }

    public SubscriptionPlan Plan { get; private set; }

    public DateTimeOffset? PlanExpiresAtUtc { get; private set; }

    /// <summary>Comma-separated feature keys switched on beyond the plan.</summary>
    public string? EnabledKeys { get; private set; }

    /// <summary>
    /// Comma-separated feature keys switched off despite the plan. Wins over everything.
    /// </summary>
    public string? DisabledKeys { get; private set; }

    /// <summary>
    /// Why the last override was made. Required, because six months later nobody remembers
    /// whether a feature is off because it broke or because a committee asked, and the two
    /// have opposite answers to "can we turn it back on".
    /// </summary>
    public string? LastChangeReason { get; private set; }

    public DateTimeOffset? LastChangedAtUtc { get; private set; }

    public void ChangePlan(SubscriptionPlan plan, DateTimeOffset? expiresAtUtc, DateTimeOffset nowUtc)
    {
        Plan = plan;
        PlanExpiresAtUtc = expiresAtUtc;
        LastChangedAtUtc = nowUtc;
    }

    public void Enable(string featureKey, string reason, DateTimeOffset nowUtc)
    {
        EnabledKeys = Add(EnabledKeys, featureKey);
        DisabledKeys = Remove(DisabledKeys, featureKey);
        Record(reason, nowUtc);
    }

    public void Disable(string featureKey, string reason, DateTimeOffset nowUtc)
    {
        DisabledKeys = Add(DisabledKeys, featureKey);
        EnabledKeys = Remove(EnabledKeys, featureKey);
        Record(reason, nowUtc);
    }

    /// <summary>Drops an override so the plan decides again.</summary>
    public void Clear(string featureKey, string reason, DateTimeOffset nowUtc)
    {
        EnabledKeys = Remove(EnabledKeys, featureKey);
        DisabledKeys = Remove(DisabledKeys, featureKey);
        Record(reason, nowUtc);
    }

    public SocietyEntitlements ToSnapshot() => new()
    {
        SocietyId = SocietyId,
        Plan = Plan,
        PlanExpiresAtUtc = PlanExpiresAtUtc,
        Enabled = Split(EnabledKeys),
        Disabled = Split(DisabledKeys),
    };

    private void Record(string reason, DateTimeOffset nowUtc)
    {
        LastChangeReason = reason;
        LastChangedAtUtc = nowUtc;
    }

    private static IReadOnlyList<string> Split(string? keys) =>
        string.IsNullOrWhiteSpace(keys)
            ? []
            : [.. keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string? Add(string? keys, string featureKey)
    {
        var set = new HashSet<string>(Split(keys), StringComparer.OrdinalIgnoreCase) { featureKey };
        return string.Join(',', set);
    }

    private static string? Remove(string? keys, string featureKey)
    {
        var set = new HashSet<string>(Split(keys), StringComparer.OrdinalIgnoreCase);
        set.Remove(featureKey);
        return set.Count == 0 ? null : string.Join(',', set);
    }
}

/// <summary>
/// A feature's rollout wave. Platform-scoped, not society-scoped — the whole point is to reason
/// about one feature across every society at once.
/// </summary>
public sealed class FeatureRolloutRecord : Entity
{
    private FeatureRolloutRecord() { }

    public FeatureRolloutRecord(Guid id, string featureKey)
        : base(id)
    {
        FeatureKey = featureKey;
        Stage = RolloutStage.Off;
    }

    public string FeatureKey { get; private set; } = string.Empty;

    public RolloutStage Stage { get; private set; }

    /// <summary>Comma-separated society ids in the pilot wave.</summary>
    public string? PilotSocietyIds { get; private set; }

    public int Percentage { get; private set; }

    public DateTimeOffset? LastAdvancedAtUtc { get; private set; }

    /// <summary>
    /// Widens the wave. Only ever forwards through the stages, except to Off — which is the
    /// abort, and has to stay available from anywhere.
    /// </summary>
    public void Advance(RolloutStage stage, int percentage, DateTimeOffset nowUtc)
    {
        Stage = stage;
        Percentage = stage == RolloutStage.Percentage ? Math.Clamp(percentage, 0, 100) : 0;
        LastAdvancedAtUtc = nowUtc;
    }

    public void SetPilot(IEnumerable<Guid> societyIds, DateTimeOffset nowUtc)
    {
        PilotSocietyIds = string.Join(',', societyIds);
        Stage = RolloutStage.Pilot;
        LastAdvancedAtUtc = nowUtc;
    }

    public FeatureRollout ToRollout() => new()
    {
        FeatureKey = FeatureKey,
        Stage = Stage,
        Percentage = Percentage,
        PilotSocietyIds = string.IsNullOrWhiteSpace(PilotSocietyIds)
            ? []
            : [.. PilotSocietyIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Guid.Parse)],
    };
}
