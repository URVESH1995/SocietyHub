using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SocietyHub.SharedKernel.Features;

namespace SocietyHub.Features;

/// <summary>
/// Refuses a request for a feature the society does not have.
///
/// A filter rather than a check inside each handler, because the failure mode of the latter is
/// silent: somebody adds a second endpoint for the same feature and forgets the guard, and
/// nothing tells them. Attached at the route it is visible in the endpoint definition and in
/// the generated OpenAPI document.
/// </summary>
public sealed class RequireFeatureFilter : IEndpointFilter
{
    private readonly string _featureKey;
    private readonly bool _hideExistence;

    public RequireFeatureFilter(string featureKey, bool hideExistence)
    {
        _featureKey = featureKey;
        _hideExistence = hideExistence;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var gate = context.HttpContext.RequestServices.GetRequiredService<IFeatureGate>();

        if (await gate.IsEnabledAsync(_featureKey, context.HttpContext.RequestAborted))
        {
            return await next(context);
        }

        // 404 where merely confirming the endpoint exists would leak something — a society
        // should not be able to discover which of its neighbours run face recognition by
        // probing for a 402.
        if (_hideExistence)
        {
            return Results.NotFound();
        }

        var upgradeTo = PlanCatalogue.LowestPlanFor(_featureKey);

        // 402 rather than 403: the caller is permitted, the society has not bought it. A
        // client that cannot tell those apart will tell a resident they lack permission when
        // the real answer is that their committee needs to upgrade.
        return Results.Problem(
            statusCode: StatusCodes.Status402PaymentRequired,
            title: "Feature not enabled",
            detail: upgradeTo is null
                ? $"'{_featureKey}' is not enabled for this society."
                : $"'{_featureKey}' requires the {upgradeTo} plan.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "feature.not_enabled",
                ["feature"] = _featureKey,
                ["requiredPlan"] = upgradeTo?.ToString(),
            });
    }
}

public static class RequireFeatureExtensions
{
    /// <summary>
    /// Gates an endpoint behind a feature.
    ///
    /// Set <paramref name="hideExistence"/> for anything whose mere presence is sensitive —
    /// face recognition being the case that prompted the parameter.
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(
        this TBuilder builder, string featureKey, bool hideExistence = false)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(new RequireFeatureFilter(featureKey, hideExistence));

        builder.WithMetadata(new FeatureRequirementMetadata(featureKey));

        return builder;
    }
}

/// <summary>
/// Marks which feature an endpoint needs, so the OpenAPI document and the generated client
/// SDK can carry it. A client that knows an endpoint needs <c>drives.enabled</c> can hide the
/// tab instead of showing a button that only ever returns 402.
/// </summary>
public sealed record FeatureRequirementMetadata(string FeatureKey);
