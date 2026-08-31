using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Features;

namespace SocietyHub.Features;

/// <summary>What a client needs to shape its UI for one society.</summary>
public sealed record FeatureManifest(
    Guid SocietyId,
    IReadOnlyList<string> Features,
    string Plan,
    DateTimeOffset RetrievedAtUtc);

public static class FeatureEndpoints
{
    /// <summary>
    /// Exposes the society's feature set so clients can hide what it does not have.
    ///
    /// This shapes the UI and enforces nothing. Every gated endpoint checks again on the
    /// server, because a client is a thing an attacker controls — a resident on Basic who
    /// edits the manifest response gets a bulk-drive tab that returns 402 on every tap, which
    /// is the correct outcome.
    ///
    /// Mapped in each service rather than centrally in the gateway so a client can ask the
    /// service it is about to call, and so the answer is still available if the gateway is
    /// serving from a stale route table.
    /// </summary>
    public static IEndpointRouteBuilder MapFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/features", async (
                IFeatureGate gate,
                ITenantContext tenant,
                IEntitlementSource source,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var societyId = tenant.RequireSocietyId();
                var features = await gate.GetEnabledFeaturesAsync(cancellationToken);
                var entitlements = await source.GetAsync(societyId, cancellationToken);

                return Results.Ok(new FeatureManifest(
                    societyId,
                    [.. features.Order()],
                    (entitlements?.Plan ?? SubscriptionPlan.Basic).ToString(),
                    timeProvider.GetUtcNow()));
            })
            .RequireAuthorization()
            .WithTags("Features")
            .WithName("GetFeatureManifest")
            .WithSummary("Lists the features enabled for the caller's society.");

        return app;
    }
}
