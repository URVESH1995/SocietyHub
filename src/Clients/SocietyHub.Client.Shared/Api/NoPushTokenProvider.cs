namespace SocietyHub.Client.Shared.Api;

/// <summary>
/// A token provider for platforms with no push integration.
///
/// Used by the Windows build, which exists only so the mobile apps can be developed without a
/// device, and by any target where Firebase or APNs is not configured. Returning null is the
/// documented "no token available" outcome that <see cref="PushRegistrationService"/> already
/// handles, so nothing downstream needs to know the difference.
/// </summary>
public sealed class NoPushTokenProvider : IDeviceTokenProvider
{
    // Never raised. Declared to satisfy the interface, and deliberately not removed from it:
    // the rotation event is the part most easily forgotten on a real platform, so it stays
    // visible in every implementation.
    public event Action<string>? TokenRefreshed
    {
        add { }
        remove { }
    }

    public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>Remembers the last registered token in ordinary preferences.</summary>
public sealed class PreferencePushTokenCache : IPushTokenCache
{
    private const string Key = "societyhub.push-token";

    private readonly Platform.IPreferenceStorage _preferences;

    public PreferencePushTokenCache(Platform.IPreferenceStorage preferences) =>
        _preferences = preferences;

    public Task<string?> GetAsync()
    {
        var stored = _preferences.Get(Key);

        // Empty reads back as "nothing registered", matching what SetAsync(null) wrote. Without
        // this an empty string would compare unequal to every real token — harmless, but it
        // would re-register on every launch after a sign-out, which is exactly the traffic the
        // cache exists to avoid.
        return Task.FromResult(string.IsNullOrEmpty(stored) ? null : stored);
    }

    public Task SetAsync(string? token)
    {
        // The empty string rather than removing the key, because IPreferenceStorage has no
        // remove and adding one for this is more surface than the case deserves.
        _preferences.Set(Key, token ?? string.Empty);
        return Task.CompletedTask;
    }
}
