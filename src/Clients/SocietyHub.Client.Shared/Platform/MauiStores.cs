using System.Text.Json;
using SocietyHub.Client.Shared.Api;
using SocietyHub.Client.Shared.Localization;

namespace SocietyHub.Client.Shared.Platform;

/// <summary>
/// The two platform primitives the mobile apps need, behind interfaces so the shared library
/// does not take a MAUI dependency.
///
/// Both apps supply the same implementations; they live here rather than being written twice
/// because the Guard app getting token storage subtly wrong is exactly the kind of divergence
/// that goes unnoticed until an audit.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key);

    Task SetAsync(string key, string value);

    void Remove(string key);
}

/// <summary>Ordinary preferences — a language choice, not a credential.</summary>
public interface IPreferenceStorage
{
    string? Get(string key);

    void Set(string key, string value);
}

/// <summary>A file the queue can survive a process kill in.</summary>
public interface IAppFileStore
{
    Task<string?> ReadAsync(string name);

    Task WriteAsync(string name, string contents);
}

/// <summary>
/// Tokens in the platform keystore.
///
/// Unlike the browser, a phone can actually defend a refresh token: Keychain on iOS and the
/// Android Keystore are backed by hardware on most devices and are not readable by other apps.
/// So the mobile builds persist the refresh token and the web build does not — the difference
/// is not an inconsistency, it is the two platforms' actual security properties.
/// </summary>
public sealed class SecureTokenStore : ITokenStore
{
    private const string AccessTokenKey = "societyhub.access-token";
    private const string RefreshTokenKey = "societyhub.refresh-token";
    private const string ExpiryKey = "societyhub.access-expiry";

    private readonly ISecretStore _storage;

    public SecureTokenStore(ISecretStore storage) => _storage = storage;

    public async Task<string?> GetAccessTokenAsync()
    {
        var raw = await _storage.GetAsync(ExpiryKey);

        if (!DateTimeOffset.TryParse(raw, out var expiry))
        {
            return null;
        }

        // Thirty seconds early, matching the clock skew the services allow. Refreshing just
        // before expiry costs one request; letting a token expire in flight costs a 401, a
        // refresh and a retry — on a weak connection at a gate, that is the difference
        // between a two-second wait and a ten-second one.
        return expiry <= DateTimeOffset.UtcNow.AddSeconds(30)
            ? null
            : await _storage.GetAsync(AccessTokenKey);
    }

    public Task<string?> GetRefreshTokenAsync() => _storage.GetAsync(RefreshTokenKey);

    public async Task SaveAsync(string accessToken, string refreshToken, DateTimeOffset expiresAtUtc)
    {
        await _storage.SetAsync(AccessTokenKey, accessToken);
        await _storage.SetAsync(RefreshTokenKey, refreshToken);
        await _storage.SetAsync(ExpiryKey, expiresAtUtc.ToString("O"));
    }

    public Task ClearAsync()
    {
        _storage.Remove(AccessTokenKey);
        _storage.Remove(RefreshTokenKey);
        _storage.Remove(ExpiryKey);

        return Task.CompletedTask;
    }
}

public sealed class PreferenceLanguageStore : ILanguageStore
{
    private const string Key = "societyhub.language";

    private readonly IPreferenceStorage _preferences;

    public PreferenceLanguageStore(IPreferenceStorage preferences) => _preferences = preferences;

    public Task<string?> GetAsync() => Task.FromResult(_preferences.Get(Key));

    public Task SetAsync(string languageTag)
    {
        _preferences.Set(Key, languageTag);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The offline queue on disk.
///
/// A plain JSON file rather than a database. The queue holds at most a few hundred small
/// records, is only ever read and written whole, and a file is one less thing to go wrong on a
/// tablet nobody will ever debug in person.
///
/// Written to a temporary file and moved into place, because the failure this guards against
/// is real: an Android tablet losing power mid-write leaves a truncated file, and a queue that
/// cannot be parsed on the next launch is a morning of gate entries gone.
/// </summary>
public sealed class FileQueueStore : IQueueStore
{
    private const string FileName = "offline-queue.json";

    private readonly IAppFileStore _files;

    public FileQueueStore(IAppFileStore files) => _files = files;

    public async Task<IReadOnlyList<QueuedAction>> LoadAsync()
    {
        var json = await _files.ReadAsync(FileName);

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<QueuedAction>>(json) ?? [];
        }
        catch (JsonException)
        {
            // Corrupt despite the atomic write — a filesystem fault, or a downgrade to a build
            // that wrote a different shape. Returning empty loses the queue, which is bad, but
            // throwing here would prevent the app starting at all, which is worse: a guard
            // with a broken tablet cannot open the gate screen to see what went wrong.
            return [];
        }
    }

    public Task SaveAsync(IReadOnlyList<QueuedAction> actions) =>
        _files.WriteAsync(FileName, JsonSerializer.Serialize(actions));
}
