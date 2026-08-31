using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Caching;
using SocietyHub.Features;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Society.Api.Persistence;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Society.Api.Features.Entitlements;

public sealed record ChangePlanRequest(
    SubscriptionPlan Plan, DateTimeOffset? ExpiresAtUtc, string Reason);

public sealed record OverrideFeatureRequest(string FeatureKey, string Reason);

public sealed record AdvanceRolloutRequest(RolloutStage Stage, int Percentage);

public sealed record SetPilotRequest(IReadOnlyList<Guid> SocietyIds);

public sealed class ChangePlanValidator : AbstractValidator<ChangePlanRequest>
{
    public ChangePlanValidator() =>
        // Six months later nobody remembers whether a plan was downgraded because a society
        // asked or because an invoice went unpaid, and those have different answers to
        // "should we restore it".
        RuleFor(r => r.Reason)
            .NotEmpty().WithErrorCode("Entitlement.ReasonRequired")
            .MinimumLength(10).WithErrorCode("Entitlement.ReasonTooShort");
}

public sealed class OverrideFeatureValidator : AbstractValidator<OverrideFeatureRequest>
{
    public OverrideFeatureValidator()
    {
        RuleFor(r => r.FeatureKey).NotEmpty().WithErrorCode("Entitlement.FeatureRequired");

        RuleFor(r => r.Reason)
            .NotEmpty().WithErrorCode("Entitlement.ReasonRequired")
            .MinimumLength(10).WithErrorCode("Entitlement.ReasonTooShort");
    }
}

/// <summary>
/// The write side of entitlements, and the only one there is.
///
/// Every route is platform-scoped. A society administrator cannot grant their own society a
/// feature — that would make the subscription an honour system — so these sit behind
/// <see cref="SocietyHubPolicies.PlatformOperations"/>, which needs both the platform claim and
/// the SuperAdmin role.
/// </summary>
public static class EntitlementEndpoints
{
    public static IEndpointRouteBuilder MapEntitlementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/entitlements")
                       .WithTags("Entitlements")
                       .RequireAuthorization(SocietyHubPolicies.PlatformOperations);

        group.MapPut("/{societyId:guid}/plan", ChangePlanAsync)
             .WithValidation<ChangePlanRequest>()
             .WithSummary("Moves a society onto a plan.");

        group.MapPost("/{societyId:guid}/enable", EnableAsync)
             .WithValidation<OverrideFeatureRequest>()
             .WithSummary("Switches a feature on for one society, beyond its plan.");

        group.MapPost("/{societyId:guid}/disable", DisableAsync)
             .WithValidation<OverrideFeatureRequest>()
             .WithSummary("Switches a feature off for one society. Beats everything else.");

        group.MapPost("/{societyId:guid}/clear", ClearAsync)
             .WithValidation<OverrideFeatureRequest>()
             .WithSummary("Drops an override so the plan decides again.");

        group.MapGet("/{societyId:guid}", GetAsync)
             .WithSummary("The resolved entitlements for one society, and why.");

        group.MapGet("/rollouts", ListRolloutsAsync)
             .WithSummary("Every feature rollout and how far it has reached.");

        group.MapPost("/rollouts/{featureKey}/pilot", SetPilotAsync)
             .WithSummary("Starts a rollout with a named set of societies.");

        group.MapPost("/rollouts/{featureKey}/advance", AdvanceAsync)
             .WithSummary("Widens or aborts a rollout.");

        return app;
    }

    private static async Task<IResult> ChangePlanAsync(
        Guid societyId,
        ChangePlanRequest request,
        SocietyDbContext context,
        ICacheStore cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var subscription = await LoadOrCreateAsync(societyId, context, cancellationToken);

        subscription.ChangePlan(request.Plan, request.ExpiresAtUtc, timeProvider.GetUtcNow());

        await context.SaveChangesAsync(cancellationToken);
        await PublishAsync(subscription, cache, cancellationToken);

        return Results.Ok(subscription.ToSnapshot());
    }

    private static Task<IResult> EnableAsync(
        Guid societyId,
        OverrideFeatureRequest request,
        SocietyDbContext context,
        ICacheStore cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        OverrideAsync(
            societyId, request, context, cache, timeProvider, cancellationToken,
            (s, key, reason, now) => s.Enable(key, reason, now));

    private static Task<IResult> DisableAsync(
        Guid societyId,
        OverrideFeatureRequest request,
        SocietyDbContext context,
        ICacheStore cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        OverrideAsync(
            societyId, request, context, cache, timeProvider, cancellationToken,
            (s, key, reason, now) => s.Disable(key, reason, now));

    private static Task<IResult> ClearAsync(
        Guid societyId,
        OverrideFeatureRequest request,
        SocietyDbContext context,
        ICacheStore cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        OverrideAsync(
            societyId, request, context, cache, timeProvider, cancellationToken,
            (s, key, reason, now) => s.Clear(key, reason, now));

    private static async Task<IResult> OverrideAsync(
        Guid societyId,
        OverrideFeatureRequest request,
        SocietyDbContext context,
        ICacheStore cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Action<SocietySubscription, string, string, DateTimeOffset> apply)
    {
        if (!KnownFeatureKeys.Contains(request.FeatureKey))
        {
            // A typo would otherwise be stored happily and silently do nothing, and the
            // operator would spend an afternoon wondering why the pilot never started.
            return Error.Validation(
                    "entitlement.unknown_feature",
                    $"'{request.FeatureKey}' is not a known feature key.")
                .ToProblem();
        }

        var subscription = await LoadOrCreateAsync(societyId, context, cancellationToken);

        apply(subscription, request.FeatureKey, request.Reason, timeProvider.GetUtcNow());

        await context.SaveChangesAsync(cancellationToken);
        await PublishAsync(subscription, cache, cancellationToken);

        return Results.Ok(subscription.ToSnapshot());
    }

    private static async Task<IResult> GetAsync(
        Guid societyId,
        SocietyDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SocietyId == societyId, cancellationToken);

        var snapshot = subscription?.ToSnapshot() ?? SocietyEntitlements.Fallback(societyId);

        var rollouts = new FeatureRolloutMap(
            await context.FeatureRollouts.AsNoTracking()
                .Select(r => r.ToRollout())
                .ToListAsync(cancellationToken));

        return Results.Ok(new
        {
            snapshot.SocietyId,
            Plan = snapshot.Plan.ToString(),
            snapshot.PlanExpiresAtUtc,
            snapshot.Enabled,
            snapshot.Disabled,
            Resolved = snapshot.Resolve(timeProvider.GetUtcNow(), rollouts).Order(),
            subscription?.LastChangeReason,
            subscription?.LastChangedAtUtc,
        });
    }

    private static async Task<IResult> ListRolloutsAsync(
        SocietyDbContext context, CancellationToken cancellationToken) =>
        Results.Ok(await context.FeatureRollouts
            .AsNoTracking()
            .OrderBy(r => r.FeatureKey)
            .Select(r => new
            {
                r.FeatureKey,
                Stage = r.Stage.ToString(),
                r.Percentage,
                r.PilotSocietyIds,
                r.LastAdvancedAtUtc,
            })
            .ToListAsync(cancellationToken));

    private static async Task<IResult> SetPilotAsync(
        string featureKey,
        SetPilotRequest request,
        SocietyDbContext context,
        ICacheStore cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!KnownFeatureKeys.Contains(featureKey))
        {
            return Error.Validation(
                "entitlement.unknown_feature", $"'{featureKey}' is not a known feature key.")
                .ToProblem();
        }

        var rollout = await LoadOrCreateRolloutAsync(featureKey, context, cancellationToken);

        rollout.SetPilot(request.SocietyIds, timeProvider.GetUtcNow());

        await context.SaveChangesAsync(cancellationToken);
        await PublishRolloutsAsync(context, cache, cancellationToken);

        return Results.Ok(rollout.ToRollout());
    }

    private static async Task<IResult> AdvanceAsync(
        string featureKey,
        AdvanceRolloutRequest request,
        SocietyDbContext context,
        ICacheStore cache,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var rollout = await LoadOrCreateRolloutAsync(featureKey, context, cancellationToken);

        rollout.Advance(request.Stage, request.Percentage, timeProvider.GetUtcNow());

        await context.SaveChangesAsync(cancellationToken);
        await PublishRolloutsAsync(context, cache, cancellationToken);

        return Results.Ok(rollout.ToRollout());
    }

    private static async Task<SocietySubscription> LoadOrCreateAsync(
        Guid societyId, SocietyDbContext context, CancellationToken cancellationToken)
    {
        var subscription = await context.Subscriptions
            .FirstOrDefaultAsync(s => s.SocietyId == societyId, cancellationToken);

        if (subscription is not null)
        {
            return subscription;
        }

        subscription = new SocietySubscription(
            Guid.CreateVersion7(), societyId, SubscriptionPlan.Basic);

        context.Subscriptions.Add(subscription);
        return subscription;
    }

    private static async Task<FeatureRolloutRecord> LoadOrCreateRolloutAsync(
        string featureKey, SocietyDbContext context, CancellationToken cancellationToken)
    {
        var rollout = await context.FeatureRollouts
            .FirstOrDefaultAsync(r => r.FeatureKey == featureKey, cancellationToken);

        if (rollout is not null)
        {
            return rollout;
        }

        rollout = new FeatureRolloutRecord(Guid.CreateVersion7(), featureKey);
        context.FeatureRollouts.Add(rollout);
        return rollout;
    }

    /// <summary>
    /// Pushes the snapshot other services read.
    ///
    /// Written directly rather than published as an event, and the difference matters here:
    /// disabling a misbehaving feature is an emergency action, and routing it through a broker
    /// would add a queue and a consumer between the operator pressing the button and the
    /// feature actually stopping. A cache write is immediate and every reader sees it on their
    /// next request. If Redis is down the readers fall back to the baseline anyway, which is
    /// the same conservative answer.
    /// </summary>
    private static async Task PublishAsync(
        SocietySubscription subscription, ICacheStore cache, CancellationToken cancellationToken)
    {
        await cache.SetAsync(
            CacheKey.ForSociety(subscription.SocietyId, "entitlements", "snapshot"),
            subscription.ToSnapshot(),
            TimeSpan.FromHours(24),
            cancellationToken);

        // Evicts the resolved set the gate caches, so the change is visible immediately
        // rather than after the ten-minute TTL.
        await cache.RemoveAsync(
            CacheKey.ForSociety(subscription.SocietyId, "entitlements"), cancellationToken);
    }

    private static async Task PublishRolloutsAsync(
        SocietyDbContext context, ICacheStore cache, CancellationToken cancellationToken)
    {
        var rollouts = await context.FeatureRollouts
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        await cache.SetAsync(
            CacheKey.ForPlatformWideData("rollouts"),
            rollouts.Select(r => r.ToRollout()).ToList(),
            TimeSpan.FromHours(24),
            cancellationToken);
    }

    /// <summary>
    /// Every key <see cref="SocietyHub.SharedKernel.Features.FeatureKey"/> declares, read by
    /// reflection so adding a constant there is the only step needed to make it settable.
    /// </summary>
    private static readonly HashSet<string> KnownFeatureKeys =
        new(typeof(SocietyHub.SharedKernel.Features.FeatureKey)
                .GetFields(System.Reflection.BindingFlags.Public
                           | System.Reflection.BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, FieldType: { } t } && t == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!),
            StringComparer.OrdinalIgnoreCase);
}
