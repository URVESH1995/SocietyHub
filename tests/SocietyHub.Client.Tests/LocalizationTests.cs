using System.Globalization;
using System.Xml.Linq;
using SocietyHub.Client.Shared.Localization;

namespace SocietyHub.Client.Tests;

/// <summary>
/// Translation completeness.
///
/// A missing Hindi key does not fail — it silently falls back to English. A resident who chose
/// Hindi then sees a screen half in each language and cannot tell whether the English part is
/// untranslated or is telling them something different. Nobody reports that as a bug, so it has
/// to be caught here.
/// </summary>
public sealed class ResourceParityTests
{
    private static readonly string ResourceDirectory = FindResourceDirectory();

    private static IReadOnlyDictionary<string, string> Load(string fileName)
    {
        var document = XDocument.Load(Path.Combine(ResourceDirectory, fileName));

        return document
            .Root!
            .Elements("data")
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty);
    }

    [Fact]
    public void Every_english_key_has_a_hindi_translation()
    {
        var english = Load("Strings.resx");
        var hindi = Load("Strings.hi-IN.resx");

        var missing = english.Keys.Except(hindi.Keys).Order().ToList();

        Assert.True(
            missing.Count == 0,
            $"Missing Hindi translations: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Hindi_has_no_keys_english_does_not()
    {
        // The other direction matters too: a key only in Hindi is one that was renamed in
        // English and left behind, and it will never be shown to anyone.
        var english = Load("Strings.resx");
        var hindi = Load("Strings.hi-IN.resx");

        var orphaned = hindi.Keys.Except(english.Keys).Order().ToList();

        Assert.True(
            orphaned.Count == 0,
            $"Hindi keys with no English original: {string.Join(", ", orphaned)}");
    }

    [Fact]
    public void No_translation_is_empty()
    {
        // An empty value is worse than a missing one: it renders as a blank label rather than
        // falling back to English, so the button has no text at all.
        foreach (var file in new[] { "Strings.resx", "Strings.hi-IN.resx" })
        {
            var blank = Load(file)
                .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key)
                .ToList();

            Assert.True(blank.Count == 0, $"{file} has empty values: {string.Join(", ", blank)}");
        }
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        // "{0} तक" must keep its {0}. A translation that drops a placeholder throws a
        // FormatException at runtime, on a screen, in front of a resident — and only for
        // people using that language, which is why it survives testing.
        var english = Load("Strings.resx");
        var hindi = Load("Strings.hi-IN.resx");

        foreach (var (key, englishValue) in english)
        {
            if (!hindi.TryGetValue(key, out var hindiValue))
            {
                continue;
            }

            Assert.Equal(PlaceholdersIn(englishValue), PlaceholdersIn(hindiValue));
        }
    }

    private static IReadOnlyList<string> PlaceholdersIn(string value) =>
        [.. System.Text.RegularExpressions.Regex
            .Matches(value, @"\{\d+\}")
            .Select(m => m.Value)
            .Distinct()
            .Order()];

    /// <summary>
    /// Finds the resx files by walking up from the test output directory.
    ///
    /// Reading the XML directly rather than the compiled resources, because the point is to
    /// assert the source files are complete. Compiled satellite assemblies would already have
    /// applied the fallback this test exists to detect.
    /// </summary>
    private static string FindResourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src", "Clients", "SocietyHub.Client.Shared", "Localization");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the shared client's Localization directory.");
    }
}

/// <summary>
/// Language resolution: a stated choice outranks a device setting, which outranks a default.
/// </summary>
public sealed class LanguageServiceTests
{
    private sealed class InMemoryLanguageStore : ILanguageStore
    {
        private string? _value;

        public InMemoryLanguageStore(string? initial = null) => _value = initial;

        public Task<string?> GetAsync() => Task.FromResult(_value);

        public Task SetAsync(string languageTag)
        {
            _value = languageTag;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task An_explicit_choice_wins_over_the_society_default()
    {
        // A resident who picked Hindi keeps Hindi even in a society whose committee set the
        // default to English. The choice they made by hand outranks a setting made for them.
        var service = new LanguageService(new InMemoryLanguageStore("hi-IN"));

        await service.InitialiseAsync(societyDefault: "en-IN");

        Assert.Equal("hi-IN", service.Current.Value);
    }

    [Fact]
    public async Task The_society_default_applies_when_nothing_was_chosen()
    {
        var service = new LanguageService(new InMemoryLanguageStore());

        await service.InitialiseAsync(societyDefault: "hi-IN");

        Assert.Equal("hi-IN", service.Current.Value);
    }

    [Fact]
    public async Task An_unsupported_stored_language_falls_back_rather_than_crashing()
    {
        // A preference for a language the app once shipped and no longer does must not leave
        // someone staring at a crash on launch.
        var service = new LanguageService(new InMemoryLanguageStore("ta-IN"));

        await service.InitialiseAsync(societyDefault: null);

        Assert.Equal("en-IN", service.Current.Value);
    }

    [Fact]
    public async Task Choosing_a_language_persists_it()
    {
        var store = new InMemoryLanguageStore();
        var service = new LanguageService(store);

        await service.SetAsync(
            SocietyHub.SharedKernel.Globalization.LanguageTag.Create("hi-IN").Value);

        Assert.Equal("hi-IN", await store.GetAsync());
    }

    [Fact]
    public async Task Changing_language_sets_both_cultures()
    {
        // Both, and they are not the same thing: CurrentUICulture picks the resource file and
        // CurrentCulture formats dates and numbers. Setting only the first gives a resident
        // Hindi text over dates in US format.
        var service = new LanguageService(new InMemoryLanguageStore());

        await service.SetAsync(
            SocietyHub.SharedKernel.Globalization.LanguageTag.Create("hi-IN").Value);

        Assert.Equal("hi-IN", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("hi-IN", CultureInfo.CurrentCulture.Name);
    }

    [Fact]
    public void Only_fully_translated_languages_are_offered()
    {
        // The switcher lists exactly what the shared kernel says is supported. A language
        // appearing here without complete resources is how a resident ends up with a
        // half-translated app.
        var offered = LanguageService.Available.Select(o => o.Tag).Order().ToList();

        Assert.Equal(
            SocietyHub.SharedKernel.Globalization.LanguageTag.Supported.Order(),
            offered);
    }

    [Fact]
    public void Every_option_is_labelled_in_its_own_script()
    {
        // Someone who cannot read English cannot find "Hindi" in a list, but they can find
        // "हिन्दी". This is the difference between a switcher that works for the people who
        // need it and one that only works for people who do not.
        var hindi = LanguageService.Available.Single(o => o.Tag == "hi-IN");

        Assert.Equal("हिन्दी", hindi.NativeName);
        Assert.NotEqual(hindi.EnglishName, hindi.NativeName);
    }
}
