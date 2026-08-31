using SocietyHub.Features;
using SocietyHub.SharedKernel.Features;

namespace SocietyHub.Features.Tests;

/// <summary>
/// Resolution order decides what a society can do and what it has to pay for. Both directions
/// are expensive to get wrong: giving away Premium features loses revenue quietly, and
/// withholding paid ones produces a support ticket the same afternoon.
/// </summary>
public sealed class EntitlementResolutionTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static SocietyEntitlements Entitlements(
        SubscriptionPlan plan = SubscriptionPlan.Standard,
        string[]? enabled = null,
        string[]? disabled = null,
        DateTimeOffset? expires = null) => new()
    {
        SocietyId = SocietyId,
        Plan = plan,
        Enabled = enabled ?? [],
        Disabled = disabled ?? [],
        PlanExpiresAtUtc = expires,
    };

    [Fact]
    public void A_plan_grants_its_own_features()
    {
        var resolved = Entitlements(SubscriptionPlan.Standard).Resolve(Now);

        Assert.Contains(FeatureKey.BulkServiceDrives, resolved);
        Assert.Contains(FeatureKey.VisitorManagement, resolved);
    }

    [Fact]
    public void A_plan_does_not_grant_a_higher_plans_features()
    {
        Assert.DoesNotContain(
            FeatureKey.CameraAnpr, Entitlements(SubscriptionPlan.Standard).Resolve(Now));
    }

    [Fact]
    public void An_override_grants_a_feature_the_plan_does_not()
    {
        // This is what makes a pilot possible: five societies run next year's feature while
        // still paying this year's price.
        var resolved = Entitlements(
            SubscriptionPlan.Basic, enabled: [FeatureKey.CameraAnpr]).Resolve(Now);

        Assert.Contains(FeatureKey.CameraAnpr, resolved);
    }

    [Fact]
    public void A_disable_beats_the_plan()
    {
        var resolved = Entitlements(
            SubscriptionPlan.Standard, disabled: [FeatureKey.BulkServiceDrives]).Resolve(Now);

        Assert.DoesNotContain(FeatureKey.BulkServiceDrives, resolved);
    }

    [Fact]
    public void A_disable_beats_an_enable()
    {
        // The most important ordering in the file. When a feature misbehaves for one society
        // at 2am, the person switching it off must not be second-guessed by another row.
        var resolved = Entitlements(
            SubscriptionPlan.Basic,
            enabled: [FeatureKey.CameraAnpr],
            disabled: [FeatureKey.CameraAnpr]).Resolve(Now);

        Assert.DoesNotContain(FeatureKey.CameraAnpr, resolved);
    }

    [Fact]
    public void A_disable_beats_a_rollout()
    {
        var rollouts = new FeatureRolloutMap([
            new FeatureRollout { FeatureKey = FeatureKey.CameraAnpr, Stage = RolloutStage.On },
        ]);

        var resolved = Entitlements(
            SubscriptionPlan.Premium, disabled: [FeatureKey.CameraAnpr]).Resolve(Now, rollouts);

        Assert.DoesNotContain(FeatureKey.CameraAnpr, resolved);
    }

    [Fact]
    public void A_lapsed_plan_falls_back_to_the_baseline_not_to_nothing()
    {
        // An unpaid invoice must not leave a guard unable to log a visitor. The society loses
        // what it stopped paying for and keeps what every society has.
        var resolved = Entitlements(
            SubscriptionPlan.Premium, expires: Now.AddDays(-1)).Resolve(Now);

        Assert.Contains(FeatureKey.VisitorManagement, resolved);
        Assert.Contains(FeatureKey.Complaints, resolved);
        Assert.DoesNotContain(FeatureKey.CameraAnpr, resolved);
        Assert.DoesNotContain(FeatureKey.BulkServiceDrives, resolved);
    }

    [Fact]
    public void A_plan_that_expires_later_today_is_still_in_force()
    {
        var resolved = Entitlements(
            SubscriptionPlan.Premium, expires: Now.AddHours(1)).Resolve(Now);

        Assert.Contains(FeatureKey.CameraAnpr, resolved);
    }

    [Fact]
    public void A_lapsed_plan_still_honours_an_explicit_enable()
    {
        // A society mid-renegotiation that has been promised a feature keeps it. The expiry
        // downgrades the plan, not the deliberate decisions made on top of it.
        var resolved = Entitlements(
            SubscriptionPlan.Premium,
            enabled: [FeatureKey.BulkServiceDrives],
            expires: Now.AddDays(-1)).Resolve(Now);

        Assert.Contains(FeatureKey.BulkServiceDrives, resolved);
    }

    [Fact]
    public void The_fallback_is_exactly_the_baseline()
    {
        // Load-bearing: this is what every service resolves to when the entitlement store is
        // unreachable. It must be neither empty nor generous.
        var resolved = SocietyEntitlements.Fallback(SocietyId).Resolve(Now);

        Assert.Equal(PlanCatalogue.Baseline.Order(), resolved.Order());
    }
}

/// <summary>
/// The plan table is a product decision. These assert the parts of it that would cost real
/// money or real trust to get wrong.
/// </summary>
public sealed class PlanCatalogueTests
{
    [Fact]
    public void Every_plan_includes_the_gate_and_complaints()
    {
        // A half-adopted platform at the gate is worse than none: guards fall back to the
        // paper register and the digital log silently goes stale. So even a society paying
        // nothing gets the gate.
        foreach (var plan in Enum.GetValues<SubscriptionPlan>())
        {
            var features = PlanCatalogue.For(plan);

            Assert.Contains(FeatureKey.VisitorManagement, features);
            Assert.Contains(FeatureKey.Complaints, features);
            Assert.Contains(FeatureKey.SosAlert, features);
        }
    }

    [Fact]
    public void Each_plan_contains_everything_below_it()
    {
        // An upgrade must never take a feature away. Without this, reordering the sets by hand
        // could silently make Premium a downgrade from Standard for one capability.
        Assert.Subset(
            (HashSet<string>)PlanCatalogue.For(SubscriptionPlan.Standard),
            (HashSet<string>)PlanCatalogue.For(SubscriptionPlan.Basic));

        Assert.Subset(
            (HashSet<string>)PlanCatalogue.For(SubscriptionPlan.Premium),
            (HashSet<string>)PlanCatalogue.For(SubscriptionPlan.Standard));
    }

    [Fact]
    public void No_plan_grants_face_recognition_at_any_price()
    {
        // The one feature that cannot be reached by upgrading a subscription. It processes
        // biometrics, needs a signed agreement, and each resident must separately consent —
        // none of which a payment page can establish.
        foreach (var plan in Enum.GetValues<SubscriptionPlan>())
        {
            Assert.DoesNotContain(FeatureKey.ResidentFaceEntry, PlanCatalogue.For(plan));
        }

        Assert.Null(PlanCatalogue.LowestPlanFor(FeatureKey.ResidentFaceEntry));
    }

    [Fact]
    public void The_upgrade_prompt_names_the_cheapest_plan_that_works()
    {
        // Telling a Basic society to buy Premium for a feature Standard includes is a way to
        // lose the sale twice.
        Assert.Equal(
            SubscriptionPlan.Standard, PlanCatalogue.LowestPlanFor(FeatureKey.BulkServiceDrives));

        Assert.Equal(
            SubscriptionPlan.Basic, PlanCatalogue.LowestPlanFor(FeatureKey.VisitorManagement));

        Assert.Equal(
            SubscriptionPlan.Premium, PlanCatalogue.LowestPlanFor(FeatureKey.CameraAnpr));
    }
}
