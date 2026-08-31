using SocietyHub.SharedKernel.Features;

namespace SocietyHub.Features;

/// <summary>
/// What a society pays for. Ordered, so a comparison means something.
/// </summary>
public enum SubscriptionPlan
{
    /// <summary>
    /// Free or near-free. The gate and complaints — enough that a society that never pays a
    /// rupee still gets real value, because a half-adopted platform at the gate is worse than
    /// none: guards fall back to the paper register and the digital log silently goes stale.
    /// </summary>
    Basic = 0,

    /// <summary>The paying default. Adds bulk drives, payments and the directory.</summary>
    Standard = 1,

    /// <summary>Adds camera AI, which is the only tier with a real marginal cost.</summary>
    Premium = 2,
}

/// <summary>
/// Which features each plan grants.
///
/// A static table rather than rows in a database, deliberately. The plan-to-feature mapping is
/// a product decision that ships with a release and is reviewed in a pull request; making it
/// editable at runtime means the difference between plans can drift per environment and nobody
/// can say what Standard actually includes. Per-<em>society</em> variation is the thing that
/// has to be data, and it is — see <see cref="SocietyEntitlements"/>.
/// </summary>
public static class PlanCatalogue
{
    /// <summary>
    /// What every society gets regardless of plan, and regardless of whether the entitlement
    /// store can be reached.
    ///
    /// This set is load-bearing in an unobvious way: it is also the fallback when the
    /// entitlement cache is cold and the source is unreachable. A guard mid-shift must not
    /// find the gate switched off because Redis restarted, and equally a Redis outage must not
    /// hand a Basic society the camera features it never paid for. Failing back to exactly
    /// what every plan already includes satisfies both.
    /// </summary>
    public static readonly IReadOnlySet<string> Baseline = new HashSet<string>
    {
        FeatureKey.VisitorManagement,
        FeatureKey.DeliveryEntry,
        FeatureKey.DailyHelpAttendance,
        FeatureKey.Complaints,
        FeatureKey.NoticeBoard,
        FeatureKey.SosAlert,
    };

    private static readonly IReadOnlySet<string> StandardFeatures = new HashSet<string>(Baseline)
    {
        FeatureKey.ResidentDirectory,
        FeatureKey.BulkServiceDrives,
        FeatureKey.OnlinePayments,
        FeatureKey.VendorMarketplace,
        FeatureKey.CommitteeVoting,
    };

    private static readonly IReadOnlySet<string> PremiumFeatures =
        new HashSet<string>(StandardFeatures)
        {
            FeatureKey.CameraAnpr,
            FeatureKey.CameraTailgating,
            FeatureKey.CameraParking,
            FeatureKey.CameraIntrusion,
            FeatureKey.CameraFleetHealth,
            FeatureKey.CameraFireDetection,
            FeatureKey.CameraFallDetection,
            FeatureKey.CameraPoolSafety,
            FeatureKey.MaintenanceBilling,
            FeatureKey.AmenityBooking,
            FeatureKey.ParkingManagement,
            FeatureKey.DocumentVault,

            // Note what is absent: FeatureKey.ResidentFaceEntry. No plan grants it, at any
            // price. It is enabled per society by explicit override after a signed agreement,
            // and even then each resident must separately consent. A feature that processes
            // biometrics must not be reachable by upgrading a subscription.
        };

    public static IReadOnlySet<string> For(SubscriptionPlan plan) => plan switch
    {
        SubscriptionPlan.Premium => PremiumFeatures,
        SubscriptionPlan.Standard => StandardFeatures,
        _ => Baseline,
    };

    /// <summary>
    /// The lowest plan that includes a feature, for the "upgrade to unlock" message a client
    /// shows. Null when no plan grants it — the answer there is not an upgrade.
    /// </summary>
    public static SubscriptionPlan? LowestPlanFor(string featureKey)
    {
        foreach (var plan in Enum.GetValues<SubscriptionPlan>().Order())
        {
            if (For(plan).Contains(featureKey))
            {
                return plan;
            }
        }

        return null;
    }
}
