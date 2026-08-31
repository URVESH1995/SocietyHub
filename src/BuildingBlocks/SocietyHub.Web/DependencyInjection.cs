using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocietyHub.Caching;
using SocietyHub.Features;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.Web.Globalization;
using SocietyHub.Web.Idempotency;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Tenancy;
using SocietyHub.Web.Versioning;
using System.Reflection;

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

    /// <summary>
    /// Everything an API service needs from the platform: request contexts, authentication and
    /// policies, caching and locking, validation, and problem-details error handling.
    /// </summary>
    /// <param name="validatorAssembly">
    /// Assembly scanned for FluentValidation validators — normally the calling service's.
    /// </param>
    public static IServiceCollection AddSocietyHubPlatform(
        this IServiceCollection services,
        IConfiguration configuration,
        Assembly validatorAssembly)
    {
        services.AddSocietyHubRequestContext();
        services.AddSocietyHubAuthentication(configuration);
        services.AddSocietyHubCaching();

        // The read-side feature gate. Society overrides this with its own database-backed
        // source before calling here, and the TryAdd inside leaves that alone.
        services.AddSocietyHubFeatures(configuration);

        services.AddValidatorsFromAssembly(validatorAssembly, includeInternalTypes: true);

        // ProblemDetails for framework-generated failures too — a 404 from routing should look
        // like a 404 from a handler, so clients parse one shape rather than two.
        services.Configure<ClientVersionOptions>(
            configuration.GetSection(ClientVersionOptions.SectionName));

        services.AddProblemDetails();
        services.AddExceptionHandler<SocietyHubExceptionHandler>();

        return services;
    }

    /// <summary>
    /// The middleware order that matters.
    ///
    /// Exception handling is outermost so it catches everything after it. The client-version
    /// gate runs next, before authentication, so an unsupported build gets an actionable 426
    /// rather than a 401 it will interpret as a login problem. Idempotency sits
    /// after authentication because its key is scoped by society and user, and before the
    /// endpoints because its whole job is to avoid running one twice.
    /// </summary>
    public static WebApplication UseSocietyHubPlatform(this WebApplication app)
    {
        app.UseExceptionHandler();

        // Before authentication: a build too old to be supported is refused whether or not
        // its token is still valid, and a refusal it can act on beats a 401 it cannot.
        app.UseMiddleware<ClientVersionMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseSocietyHubIdempotency();

        return app;
    }
}
