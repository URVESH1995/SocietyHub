using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocietyHub.SharedKernel.Features;

namespace SocietyHub.Features;

public static class FeatureRegistration
{
    /// <summary>
    /// Registers the read-side feature gate. Used by every service except Society, which
    /// owns the data and registers its own <see cref="IEntitlementSource"/> before calling
    /// this — <c>TryAdd</c> leaves that registration alone.
    /// </summary>
    public static IServiceCollection AddSocietyHubFeatures(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new FeatureGateOptions();
        configuration.GetSection(FeatureGateOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.TryAddScoped<IEntitlementSource, CachedEntitlementSource>();
        services.TryAddScoped<IFeatureGate, CachedFeatureGate>();

        return services;
    }
}
