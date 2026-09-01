using Microsoft.Extensions.Logging;
using SocietyHub.Client.Shared;
using SocietyHub.Client.Shared.Api;
using SocietyHub.Client.Shared.Localization;
using SocietyHub.Client.Shared.Platform;
using SocietyHub.Resident.App.Platform;

namespace SocietyHub.Resident.App;

// SocietyHub resident app.
//
// MAUI Blazor Hybrid, so the screens are the same Razor components the admin console uses and
// there is one implementation of a notice card rather than three that drift.
//
// This is the build with the longest tail: a resident's phone can hold it for eighteen months,
// cannot be force-updated, and app-store review means even a fix takes days to reach anyone.
// Everything about the versioning and deprecation design exists because of this app.

public static class MauiProgram
{
    /// <summary>
    /// Where the app talks to. A build-time constant rather than something a user can change —
    /// a resident who can point the app at another host is a phishing surface, and there is no
    /// legitimate reason for one to need it.
    /// </summary>
    private const string GatewayBaseAddress = "https://api.societyhub.in/";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();

        builder.Services.AddSingleton<ISecretStore, MauiSecureStorage>();
        builder.Services.AddSingleton<IPreferenceStorage, MauiPreferenceStorage>();
        builder.Services.AddSingleton<IAppFileStore, MauiFileStore>();

        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<ILanguageStore, PreferenceLanguageStore>();
        builder.Services.AddSingleton<IQueueStore, FileQueueStore>();

        builder.Services.AddSocietyHubClient(
            new Uri(GatewayBaseAddress),

            // The platform string the server's deprecation rules are keyed on. Android and iOS
            // are reported separately because their update behaviour differs enough to justify
            // different retirement schedules.
            new ClientIdentity(Platform(), AppInfo.Current.VersionString));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    private static string Platform() => DeviceInfo.Current.Platform == DevicePlatform.iOS
        ? "ios"
        : "android";
}
