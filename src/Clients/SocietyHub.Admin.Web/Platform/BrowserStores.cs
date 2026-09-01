using Blazored.LocalStorage;
using SocietyHub.Client.Shared.Api;
using SocietyHub.Client.Shared.Localization;

namespace SocietyHub.Admin.Web.Platform;

/// <summary>
/// Token storage in a browser, with the compromise stated rather than hidden.
///
/// Browser storage cannot be protected from script running on the same origin. A refresh token
/// there is a long-lived credential one XSS away from being stolen, and refresh rotation does
/// not help — an attacker who steals it simply uses it first.
///
/// So the web build keeps the refresh token in memory only. The cost is real: closing the tab
/// signs the user out. That is acceptable here because this is the committee and admin console,
/// used at a desk in sessions, not the resident app someone opens for ten seconds at a gate.
/// The mobile builds keep theirs in the Keychain or Keystore, where the platform can actually
/// defend it.
/// </summary>
public sealed class BrowserTokenStore : ITokenStore
{
    private const string AccessTokenKey = "societyhub.access-token";
    private const string ExpiryKey = "societyhub.access-expiry";

    private readonly ILocalStorageService _storage;

    private string? _refreshToken;

    public BrowserTokenStore(ILocalStorageService storage) => _storage = storage;

    public async Task<string?> GetAccessTokenAsync()
    {
        var expiry = await _storage.GetItemAsync<DateTimeOffset?>(ExpiryKey);

        // Treated as expired a little early. A token that expires in flight produces a 401
        // the client then has to unpick; refreshing thirty seconds sooner avoids the round
        // trip entirely and matches the clock skew the services allow.
        if (expiry is null || expiry <= DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return null;
        }

        return await _storage.GetItemAsStringAsync(AccessTokenKey);
    }

    public Task<string?> GetRefreshTokenAsync() => Task.FromResult(_refreshToken);

    public async Task SaveAsync(string accessToken, string refreshToken, DateTimeOffset expiresAtUtc)
    {
        // In memory, never persisted. See the type remarks.
        _refreshToken = refreshToken;

        await _storage.SetItemAsStringAsync(AccessTokenKey, accessToken);
        await _storage.SetItemAsync(ExpiryKey, expiresAtUtc);
    }

    public async Task ClearAsync()
    {
        _refreshToken = null;

        await _storage.RemoveItemAsync(AccessTokenKey);
        await _storage.RemoveItemAsync(ExpiryKey);
    }
}

/// <summary>Remembers the chosen language across visits. Not sensitive, so local storage is fine.</summary>
public sealed class BrowserLanguageStore : ILanguageStore
{
    private const string Key = "societyhub.language";

    private readonly ILocalStorageService _storage;

    public BrowserLanguageStore(ILocalStorageService storage) => _storage = storage;

    public async Task<string?> GetAsync() => await _storage.GetItemAsStringAsync(Key);

    public async Task SetAsync(string languageTag) =>
        await _storage.SetItemAsStringAsync(Key, languageTag);
}

/// <summary>
/// The admin console does not queue work offline.
///
/// Deliberate: a committee member approving a complaint from a desk can wait for the network,
/// and an offline queue in a browser tab is one closed tab away from silently losing whatever
/// was in it. The Guard app queues because a gate cannot stop; a console can.
/// </summary>
public sealed class NoOpQueueStore : IQueueStore
{
    public Task<IReadOnlyList<QueuedAction>> LoadAsync() =>
        Task.FromResult<IReadOnlyList<QueuedAction>>([]);

    public Task SaveAsync(IReadOnlyList<QueuedAction> actions) => Task.CompletedTask;
}
