using SocietyHub.Client.Shared.Platform;

namespace SocietyHub.Resident.App.Platform;

/// <summary>
/// Thin adapters onto MAUI Essentials.
///
/// They exist so the shared library can hold the real logic — token expiry, atomic queue
/// writes — without taking a MAUI dependency, which would stop the admin web app from using
/// any of it.
/// </summary>
public sealed class MauiSecureStorage : ISecretStore
{
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(key);
        }
        catch (Exception)
        {
            // Keystore access fails on some Android devices after an OS upgrade invalidates
            // the key. Treated as "no token", which sends the user to sign in again — correct,
            // and far better than a crash on launch they cannot get past.
            return null;
        }
    }

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public void Remove(string key) => SecureStorage.Default.Remove(key);
}

public sealed class MauiPreferenceStorage : IPreferenceStorage
{
    public string? Get(string key) =>
        Preferences.Default.ContainsKey(key) ? Preferences.Default.Get<string?>(key, null) : null;

    public void Set(string key, string value) => Preferences.Default.Set(key, value);
}

/// <summary>
/// Files in the app's private data directory.
///
/// Writes go to a temporary file that is then moved into place, so a tablet losing power
/// mid-write leaves the previous good file rather than a truncated one.
/// </summary>
public sealed class MauiFileStore : IAppFileStore
{
    public async Task<string?> ReadAsync(string name)
    {
        var path = PathFor(name);

        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }

    public async Task WriteAsync(string name, string contents)
    {
        var path = PathFor(name);
        var temporary = path + ".tmp";

        await File.WriteAllTextAsync(temporary, contents);

        // Move is atomic within a filesystem. Writing in place is not, and the queue is
        // exactly the file that must never be half-written.
        File.Move(temporary, path, overwrite: true);
    }

    private static string PathFor(string name) =>
        Path.Combine(FileSystem.AppDataDirectory, name);
}
