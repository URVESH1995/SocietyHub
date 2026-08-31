using SocietyHub.Features;
using SocietyHub.SharedKernel.Features;

namespace SocietyHub.Features.Tests;

/// <summary>
/// A rollout that reaches the wrong societies, or reaches the same ones every time, turns a
/// careful release into an outage — or into a handful of societies who experience the platform
/// as permanently broken.
/// </summary>
public sealed class RolloutTests
{
    private static Guid Society(int n) => Guid.Parse($"aaaaaaaa-0000-0000-0000-{n:D12}");

    [Fact]
    public void An_off_rollout_reaches_nobody()
    {
        // The state every feature ships in.
        var rollout = new FeatureRollout
        {
            FeatureKey = FeatureKey.CameraAnpr,
            Stage = RolloutStage.Off,
        };

        Assert.False(rollout.Includes(Society(1)));
    }

    [Fact]
    public void A_pilot_reaches_only_the_societies_named()
    {
        var rollout = new FeatureRollout
        {
            FeatureKey = FeatureKey.CameraAnpr,
            Stage = RolloutStage.Pilot,
            PilotSocietyIds = [Society(1), Society(2)],
        };

        Assert.True(rollout.Includes(Society(1)));
        Assert.False(rollout.Includes(Society(3)));
    }

    [Fact]
    public void A_full_rollout_reaches_everyone()
    {
        var rollout = new FeatureRollout
        {
            FeatureKey = FeatureKey.CameraAnpr,
            Stage = RolloutStage.On,
        };

        Assert.True(rollout.Includes(Society(999)));
    }

    [Fact]
    public void A_society_stays_in_the_same_bucket_across_calls()
    {
        // The property that stops a feature flickering on and off between two page loads,
        // which produces bug reports nobody can reproduce.
        var rollout = new FeatureRollout
        {
            FeatureKey = FeatureKey.CameraAnpr,
            Stage = RolloutStage.Percentage,
            Percentage = 50,
        };

        var first = rollout.Includes(Society(7));

        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(first, rollout.Includes(Society(7)));
        }
    }

    [Fact]
    public void Different_features_pick_different_societies()
    {
        // If the bucket depended only on the society, every 10% canary would land on the same
        // unlucky seventeen societies, who would come to experience the platform as broken.
        var societies = Enumerable.Range(1, 200).Select(Society).ToList();

        var inFirst = societies
            .Where(s => FeatureRollout.BucketOf(s, FeatureKey.CameraAnpr) < 20)
            .ToHashSet();

        var inSecond = societies
            .Where(s => FeatureRollout.BucketOf(s, FeatureKey.BulkServiceDrives) < 20)
            .ToHashSet();

        Assert.NotEmpty(inFirst);
        Assert.NotEmpty(inSecond);

        // Some overlap is expected and fine. Identical sets would mean the feature key is not
        // actually part of the hash.
        Assert.NotEqual(inFirst, inSecond);
    }

    [Fact]
    public void A_percentage_rollout_lands_near_its_target()
    {
        // Not an exact count — a hash bucket over 200 societies will not split perfectly, and
        // asserting that it does would make this test fail on a correct implementation. The
        // real claim is that 20% is roughly a fifth and nowhere near a half or nothing.
        var included = Enumerable.Range(1, 200)
            .Select(Society)
            .Count(s => FeatureRollout.BucketOf(s, FeatureKey.CameraAnpr) < 20);

        Assert.InRange(included, 20, 60);
    }

    [Fact]
    public void A_zero_percent_rollout_reaches_nobody()
    {
        // The boundary that matters: 0% must be off, not "whoever hashes to bucket zero".
        var rollout = new FeatureRollout
        {
            FeatureKey = FeatureKey.CameraAnpr,
            Stage = RolloutStage.Percentage,
            Percentage = 0,
        };

        Assert.All(
            Enumerable.Range(1, 200).Select(Society),
            s => Assert.False(rollout.Includes(s)));
    }

    [Fact]
    public void A_hundred_percent_rollout_reaches_everyone()
    {
        var rollout = new FeatureRollout
        {
            FeatureKey = FeatureKey.CameraAnpr,
            Stage = RolloutStage.Percentage,
            Percentage = 100,
        };

        Assert.All(
            Enumerable.Range(1, 200).Select(Society),
            s => Assert.True(rollout.Includes(s)));
    }

    [Fact]
    public void A_rollout_only_ever_widens_the_feature_set()
    {
        // A rollout is how a feature reaches people early, never how one is taken away. If it
        // could subtract, aborting a canary would strip features from societies that pay for
        // them.
        var entitlements = new SocietyEntitlements
        {
            SocietyId = Society(1),
            Plan = SubscriptionPlan.Standard,
        };

        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        var withoutRollouts = entitlements.Resolve(now);

        var withRollouts = entitlements.Resolve(now, new FeatureRolloutMap([
            new FeatureRollout { FeatureKey = FeatureKey.CameraAnpr, Stage = RolloutStage.On },
        ]));

        Assert.Subset((HashSet<string>)withRollouts, (HashSet<string>)withoutRollouts);
        Assert.Contains(FeatureKey.CameraAnpr, withRollouts);
    }
}
