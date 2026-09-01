using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocietyHub.Client.Shared.Api;
using SocietyHub.Client.Shared.Localization;

namespace SocietyHub.Client.Shared;

public static class ClientRegistration
{
    /// <summary>
    /// Registers everything three apps share.
    ///
    /// <paramref name="identity"/> differs per app and is not defaulted, because it drives the
    /// server's deprecation gate: an app that reports the wrong platform gets the wrong
    /// minimum-version rule, and a guard tablet held to the resident app's retirement schedule
    /// is a stranded gate.
    /// </summary>
    public static IServiceCollection AddSocietyHubClient(
        this IServiceCollection services, Uri baseAddress, ClientIdentity identity)
    {
        services.AddSingleton(identity);

        services.AddHttpClient<SocietyHubApiClient>(http =>
        {
            http.BaseAddress = baseAddress;

            // Longer than a typical API call, and deliberately so. A guard tablet on a weak
            // mobile connection needs a slow request to succeed rather than fail fast into a
            // queued action the guard then has to trust.
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddScoped<LanguageService>();

        services.TryAddSingleton(new OfflineQueueOptions());
        services.AddSingleton<OfflineQueue>();

        return services;
    }
}
