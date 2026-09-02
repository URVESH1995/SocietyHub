using Android.Gms.Extensions;
using Firebase.Messaging;
using SocietyHub.Client.Shared.Api;

namespace SocietyHub.Resident.App.Platforms.Android;

/// <summary>
/// The Android push token, from Firebase Cloud Messaging.
///
/// <para>
/// <b>Configuration required before this returns anything.</b> Firebase needs a project, and a
/// project is the customer's — it carries their sender id and API keys, and nobody else's will
/// do. To enable push on a real build:
/// </para>
///
/// <list type="number">
/// <item>Create an Android app in the Firebase console using the application id from the
/// csproj (<c>com.companyname.societyhub.resident.app</c>).</item>
/// <item>Download <c>google-services.json</c> into <c>Platforms/Android/</c>.</item>
/// <item>Add it to the csproj as
/// <c>&lt;GoogleServicesJson Include="Platforms\Android\google-services.json" /&gt;</c>.</item>
/// <item>Put the FCM server key into the Notification service's push provider configuration,
/// so the server can actually send to the tokens this registers.</item>
/// </list>
///
/// <para>
/// Without that file Firebase cannot initialise, and <see cref="GetTokenAsync"/> returns null
/// rather than throwing. That is deliberate: a build with no Firebase project must still run,
/// sign in and work — it simply receives no push. Crashing on launch because a config file is
/// absent would make the app undevelopable for anyone without the customer's credentials.
/// </para>
/// </summary>
public sealed class FirebaseDeviceTokenProvider : IDeviceTokenProvider
{
    public event Action<string>? TokenRefreshed;

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Throws when Firebase has no configuration, which is the normal state of a build
            // without the customer's google-services.json.
            var token = await FirebaseMessaging.Instance.GetToken().AsAsync<Java.Lang.Object>();

            return token?.ToString();
        }
        catch (Exception)
        {
            // Swallowed on purpose. Every cause here — no google-services.json, no Play
            // Services, notification permission refused, an emulator image without Google
            // APIs — has the same correct outcome: no push, and an app that still works.
            return null;
        }
    }

    /// <summary>
    /// Called by <see cref="SocietyHubFirebaseMessagingService"/> when Firebase rotates the
    /// token, which it does on reinstall and on restore from backup.
    /// </summary>
    internal void RaiseTokenRefreshed(string token) => TokenRefreshed?.Invoke(token);
}

/// <summary>
/// Receives Firebase callbacks.
///
/// Registered in the manifest by the attribute below rather than in code, because Android
/// instantiates it itself — before any of the app's own start-up has run.
/// </summary>
[global::Android.App.Service(Exported = false)]
[global::Android.App.IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class SocietyHubFirebaseMessagingService : FirebaseMessagingService
{
    /// <summary>
    /// Set during app start-up so the callback can reach the running registration service.
    ///
    /// Static because Android constructs this class, so there is no constructor to inject
    /// into. It is the narrowest seam that works, and it is why the provider exposes an
    /// internal method rather than a public one.
    /// </summary>
    internal static FirebaseDeviceTokenProvider? Provider { get; set; }

    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);

        // A rotation the app never hears about is a device that silently stops receiving
        // push — months later, with nothing connecting the two events.
        Provider?.RaiseTokenRefreshed(token);
    }
}
