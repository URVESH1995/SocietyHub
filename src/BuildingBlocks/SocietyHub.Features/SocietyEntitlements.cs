namespace SocietyHub.Features;

/// <summary>
/// Everything needed to answer "is this on for this society", in one cacheable object.
///
/// Deliberately a snapshot rather than a live query per check. A feature gate runs on nearly
/// every request; a database round trip there would put the entitlement store on the critical
/// path of the whole platform, and the data changes a few times a month.
/// </summary>
public sealed record SocietyEntitlements
{
    public required Guid SocietyId { get; init; }

    public required SubscriptionPlan Plan { get; init; }

    /// <summary>
    /// Features switched on for this society beyond its plan. This is how a pilot works: five
    /// societies run next year's feature while still paying this year's price.
    /// </summary>
    public IReadOnlyList<string> Enabled { get; init; } = [];

    /// <summary>
    /// Features switched off for this society despite its plan.
    ///
    /// Wins over everything, including an explicit enable. That ordering is the point: when a
    /// feature misbehaves for one society at 2am, somebody needs a way to stop it that cannot
    /// be second-guessed by a plan, a rollout percentage, or another row — and that does not
    /// require a deployment.
    /// </summary>
    public IReadOnlyList<string> Disabled { get; init; } = [];

    /// <summary>
    /// When the subscription lapses. A society past this date falls back to
    /// <see cref="PlanCatalogue.Baseline"/> rather than losing access outright — an unpaid
    /// invoice should not leave a guard unable to log a visitor.
    /// </summary>
    public DateTimeOffset? PlanExpiresAtUtc { get; init; }

    /// <summary>
    /// Resolves the effective feature set.
    ///
    /// Order is disable, then enable, then plan. Explicit rows beat the plan because they are
    /// the deliberate act of a human, and a disable beats an enable because it is the one
    /// that stops something going wrong.
    /// </summary>
    public IReadOnlySet<string> Resolve(DateTimeOffset nowUtc, FeatureRolloutMap? rollouts = null)
    {
        var lapsed = PlanExpiresAtUtc is not null && PlanExpiresAtUtc <= nowUtc;

        var effectivePlan = lapsed ? SubscriptionPlan.Basic : Plan;

        var resolved = new HashSet<string>(PlanCatalogue.For(effectivePlan));

        // A canary widens the set for the societies in the wave, and only ever additively —
        // a rollout is how a feature reaches people early, never how one is taken away.
        if (rollouts is not null)
        {
            foreach (var key in rollouts.FeaturesFor(SocietyId))
            {
                resolved.Add(key);
            }
        }

        foreach (var key in Enabled)
        {
            resolved.Add(key);
        }

        // Last, so it cannot be undone by anything above it.
        foreach (var key in Disabled)
        {
            resolved.Remove(key);
        }

        return resolved;
    }

    /// <summary>
    /// What a society falls back to when the entitlement store cannot be reached.
    ///
    /// See <see cref="PlanCatalogue.Baseline"/> for why this is the baseline and not either
    /// extreme.
    /// </summary>
    public static SocietyEntitlements Fallback(Guid societyId) => new()
    {
        SocietyId = societyId,
        Plan = SubscriptionPlan.Basic,
    };
}
