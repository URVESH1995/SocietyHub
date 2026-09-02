using Microsoft.Extensions.Logging;
using SocietyHub.Client.Shared;
using SocietyHub.Client.Shared.Api;
using SocietyHub.Client.Shared.Localization;
using SocietyHub.Client.Shared.Platform;
using SocietyHub.Guard.App.Platform;
using ZXing.Net.Maui.Controls;

namespace SocietyHub.Guard.App;

// SocietyHub guard app.
//
// A wall-mounted Android tablet at a gate. Three things follow from that and shape everything
// here:
//
// The connection drops. A power cut, a router reboot, a contractor cutting a cable — and the
// guard cannot stop working, because vehicles are arriving. So every write goes through the
// offline queue and syncs when the network returns. The alternative is the paper register, and
// a gate that falls back to paper tends to stay there.
//
// The device is shared and nobody owns it. A guard signs in with a shift PIN against a device
// identity, so the tablet is authenticated even when no person is.
//
// Nobody will ever debug it in person. Failures have to degrade rather than crash: a corrupt
// queue file returns empty rather than throwing, and a keystore that fails after an OS upgrade
// sends the guard to sign in rather than showing a stack trace on a gate wall.

public static class MauiProgram
{
    private const string GatewayBaseAddress = "https://api.societyhub.in/";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"))

            // Registers the ZXing handlers. Without this the camera view resolves to nothing
            // and the scan sheet opens blank — with no error anywhere.
            .UseBarcodeReader();

        builder.Services.AddMauiBlazorWebView();

        builder.Services.AddSingleton<ISecretStore, MauiSecureStorage>();
        builder.Services.AddSingleton<IPreferenceStorage, MauiPreferenceStorage>();
        builder.Services.AddSingleton<IAppFileStore, MauiFileStore>();

        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<ILanguageStore, PreferenceLanguageStore>();
        builder.Services.AddSingleton<IQueueStore, FileQueueStore>();

        // A deeper queue than the default. A gate does roughly 200 entries a day, and a
        // tablet that has been offline over a long weekend should still hold every one of
        // them rather than start refusing on the Saturday.
        builder.Services.AddSingleton(new OfflineQueueOptions { MaxDepth = 1000 });

        builder.Services.AddSocietyHubClient(
            new Uri(GatewayBaseAddress),

            // "guard", not "android". The platform string is what the server's deprecation
            // rules key on, and a guard tablet must not be retired on the resident app's
            // schedule — stranding a gate is a different order of problem from asking a
            // resident to update.
            new ClientIdentity("guard", AppInfo.Current.VersionString));

        builder.Services.AddSingleton<GateSyncService>();
        builder.Services.AddSingleton<IBarcodeScanner, MauiBarcodeScanner>();

        builder.Services.AddSingleton<IPushTokenCache, PreferencePushTokenCache>();
        builder.Services.AddSingleton<IDeviceTokenProvider, NoPushTokenProvider>();
        builder.Services.AddSingleton<PushRegistrationService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Started here rather than lazily on first use: the queue must begin draining as soon
        // as the tablet has a network, whether or not a guard has opened the gate screen.
        app.Services.GetRequiredService<GateSyncService>().Start();

        return app;
    }
}
