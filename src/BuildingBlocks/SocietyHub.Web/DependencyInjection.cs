using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.Web.Globalization;
using SocietyHub.Web.Security;
using SocietyHub.Web.Tenancy;

namespace SocietyHub.Web;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the per-request ambient contexts every service depends on. Scoped, because
    /// each is a projection of the current request's principal.
    /// </summary>
    public static IServiceCollection AddSocietyHubRequestContext(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.TryAddScoped<ITenantContext, HttpTenantContext>();
        services.TryAddScoped<ICurrentUser, HttpCurrentUser>();
        services.TryAddScoped<ILocaleContext, HttpLocaleContext>();

        // Injected rather than calling DateTimeOffset.UtcNow directly, so SLA clocks and
        // OTP expiry windows can be driven deterministically from tests.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
