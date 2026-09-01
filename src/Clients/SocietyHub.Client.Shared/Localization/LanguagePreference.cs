using System.Globalization;
using SocietyHub.SharedKernel.Globalization;

namespace SocietyHub.Client.Shared.Localization;

/// <summary>
/// Where a client remembers the language someone chose.
///
/// Implemented per platform — browser local storage on the web, preferences on mobile — because
/// there is no shared storage primitive across a Blazor WASM app and a MAUI Blazor Hybrid one,
/// and pretending otherwise would mean the web app carrying a mobile dependency.
/// </summary>
public interface ILanguageStore
{
    Task<string?> GetAsync();

    Task SetAsync(string languageTag);
}

/// <summary>
/// Resolves and changes the app's language.
///
/// The order is deliberate and matches the server's: an explicit choice, then the society's
/// default, then the device, then English. A resident who has picked Hindi keeps Hindi on a
/// device set to English, because the choice they made by hand outranks a setting they may
/// never have looked at.
/// </summary>
public sealed class LanguageService
{
    private readonly ILanguageStore _store;

    public LanguageService(ILanguageStore store) => _store = store;

    /// <summary>Fires when the language changes, so the shell can re-render.</summary>
    public event Action? LanguageChanged;

    public LanguageTag Current { get; private set; } = LanguageTag.Default;

    /// <summary>
    /// The languages a user may pick. Only those with complete translations appear — a partly
    /// translated language is worse than none, because a resident cannot tell whether a screen
    /// in English is untranslated or is telling them something different.
    /// </summary>
    public static IReadOnlyList<LanguageOption> Available { get; } =
    [
        new("en-IN", "English", "English"),
        new("hi-IN", "Hindi", "हिन्दी"),
    ];

    public async Task InitialiseAsync(string? societyDefault)
    {
        var chosen = await _store.GetAsync();

        var tag = chosen
                  ?? societyDefault
                  ?? DeviceLanguage()
                  ?? LanguageTag.Default.Value;

        // Falls back rather than failing: a stored preference for a language the app no longer
        // ships must not leave someone staring at a crash on launch.
        Apply(LanguageTag.FromHeaderOrDefault(tag));
    }

    public async Task SetAsync(LanguageTag language)
    {
        await _store.SetAsync(language.Value);
        Apply(language);
    }

    private void Apply(LanguageTag language)
    {
        Current = language;

        var culture = new CultureInfo(language.Value);

        // Both, and they are not the same thing. CurrentCulture formats dates, numbers and
        // currency; CurrentUICulture picks the resource file. Setting only the second gives a
        // resident Hindi text over dates in US format.
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// The device's language, if the app ships it.
    ///
    /// Matched on the language part alone: a phone set to <c>hi-US</c> or plain <c>hi</c> is a
    /// Hindi speaker, and refusing to match because the region differs would send them to
    /// English for no reason.
    /// </summary>
    private static string? DeviceLanguage()
    {
        var device = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        return Available
            .FirstOrDefault(o => o.Tag.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            ?.Tag;
    }
}

/// <summary>
/// A language on the switcher.
/// <paramref name="NativeName"/> is what the button says — someone who cannot read English
/// cannot find "Hindi" in a list, but they can find "हिन्दी".
/// </summary>
public sealed record LanguageOption(string Tag, string EnglishName, string NativeName);
