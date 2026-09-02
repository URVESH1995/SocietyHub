using Microsoft.Extensions.Logging;

namespace SocietyHub.Client.Shared.Api;

/// <summary>
/// Supplies the platform's push token — an FCM registration token on Android, an APNs device
/// token on iOS.
///
/// Behind an interface because obtaining it is entirely platform-specific and needs a Firebase
/// or Apple project the shared library must not know about. It also lets the whole
/// registration lifecycle below be tested without a device, which is the part that actually
/// goes wrong.
/// </summary>
public interface IDeviceTokenProvider
{
    /// <summary>
    /// The current token, or null when the platform has not issued one — permission refused,
    /// no Play Services, or simply not ready yet. Null is a normal outcome, not a failure.
    /// </summary>
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when the platform rotates the token, which it does on reinstall, restore from
    /// backup, and occasionally for its own reasons. A client that registers once at install
    /// and never listens will silently stop receiving push months later, and nobody will
    /// connect the two events.
    /// </summary>
    event Action<string>? TokenRefreshed;
}

/// <summary>
/// Remembers which token was last registered, so an unchanged one is not re-sent on every
/// launch.
/// </summary>
public interface IPushTokenCache
{
    Task<string?> GetAsync();

    Task SetAsync(string? token);
}

/// <summary>
/// Keeps the server's idea of this device's push token in step with the platform's.
///
/// Small, and worth the care: push is the channel that carries almost everything, because SMS
/// is reserved for emergencies on cost grounds. A device whose token is stale receives nothing
/// and reports no error — the server sends happily to an address that no longer exists, and
/// the resident simply stops hearing about visitors at their gate.
/// </summary>
public sealed class PushRegistrationService : IDisposable
{
    private readonly SocietyHubApiClient _api;
    private readonly IDeviceTokenProvider _provider;
    private readonly IPushTokenCache _cache;
    private readonly ILogger<PushRegistrationService> _logger;

    private bool _subscribed;

    public PushRegistrationService(
        SocietyHubApiClient api,
        IDeviceTokenProvider provider,
        IPushTokenCache cache,
        ILogger<PushRegistrationService> logger)
    {
        _api = api;
        _provider = provider;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Registers the current token if it has changed, and starts listening for rotations.
    ///
    /// Called after sign-in rather than at start-up, because the endpoint is society-scoped and
    /// needs a token of its own. Calling it while signed out produces a 401 and no registration.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_subscribed)
        {
            _provider.TokenRefreshed += OnTokenRefreshed;
            _subscribed = true;
        }

        var token = await _provider.GetTokenAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(token))
        {
            // Normal on a device where notification permission was refused, or an emulator with
            // no Play Services. Logged rather than thrown: the app works without push, it just
            // works less well, and crashing here would be a far worse trade.
            _logger.LogInformation(
                "No push token is available. This device will not receive push notifications.");

            return;
        }

        await SendIfChangedAsync(token, cancellationToken);
    }

    /// <summary>
    /// Forgets the local record on sign-out.
    ///
    /// The server's copy is deliberately left alone. It is keyed to the user who registered it,
    /// so the next sign-in on this device re-registers under whoever that is — and clearing it
    /// here would need an authenticated call at exactly the moment the token is being thrown
    /// away. Clearing the cache is what matters: it forces a re-send rather than assuming the
    /// new user's registration already exists.
    /// </summary>
    public async Task StopAsync()
    {
        if (_subscribed)
        {
            _provider.TokenRefreshed -= OnTokenRefreshed;
            _subscribed = false;
        }

        await _cache.SetAsync(null);
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            _provider.TokenRefreshed -= OnTokenRefreshed;
            _subscribed = false;
        }
    }

    private async Task SendIfChangedAsync(string token, CancellationToken cancellationToken)
    {
        var lastSent = await _cache.GetAsync();

        if (string.Equals(lastSent, token, StringComparison.Ordinal))
        {
            // Unchanged. Skipping this is not merely an optimisation — every launch of every
            // app across 42,000 flats would otherwise be a write to the notification database
            // for data that did not change.
            return;
        }

        try
        {
            await _api.RegisterPushTokenAsync(token, cancellationToken);

            // Cached only after the server confirms. Caching first means a failed call is never
            // retried, and the device goes permanently unreachable while appearing registered.
            await _cache.SetAsync(token);

            _logger.LogInformation("Registered this device for push notifications.");
        }
        catch (ApiException ex) when (ex.IsUnauthorised)
        {
            // Signed out between the check and the call. The next sign-in registers.
            _logger.LogDebug("Push registration skipped: not signed in.");
        }
        catch (Exception ex)
        {
            // Left uncached, so the next launch or token refresh tries again. Push failing to
            // register must never prevent an app from starting.
            _logger.LogWarning(ex, "Could not register the push token. It will be retried.");
        }
    }

    private void OnTokenRefreshed(string token) =>
        _ = SendIfChangedAsync(token, CancellationToken.None);
}
