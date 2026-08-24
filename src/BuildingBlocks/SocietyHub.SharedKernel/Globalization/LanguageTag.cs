using SocietyHub.SharedKernel.Results;

namespace SocietyHub.SharedKernel.Globalization;

/// <summary>
/// A BCP-47 language tag such as <c>hi-IN</c>. Stored as a tag rather than an enum so
/// adding Malayalam, or Arabic when we leave India, is a data change and not a deployment.
/// </summary>
public readonly record struct LanguageTag
{
    private LanguageTag(string value) => Value = value;

    public string Value { get; }

    public static LanguageTag Default { get; } = new("en-IN");

    /// <summary>
    /// Languages the platform ships translated notification templates and UI resources for.
    ///
    /// Deliberately short. A language listed here is a promise that every template, error
    /// string and push notification exists in it — half-translated is worse than absent,
    /// because a resident who picks Tamil and receives English SMS has been misled. Adding
    /// one is a content task, not a code change: extend this set once the resources exist.
    /// </summary>
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "en-IN", // English (India)
        "hi-IN", // Hindi
    };

    /// <summary>
    /// Next in line, in rough order of Indian urban-housing demand. Listed so the roadmap
    /// is visible in code, and so nobody re-derives the ordering later.
    /// </summary>
    public static IReadOnlyList<string> Planned { get; } =
    [
        "mr-IN", // Marathi
        "gu-IN", // Gujarati
        "ta-IN", // Tamil
        "te-IN", // Telugu
        "kn-IN", // Kannada
        "bn-IN", // Bengali
        "ml-IN", // Malayalam
        "pa-IN", // Punjabi
    ];

    public static Result<LanguageTag> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation("Language.Empty", "A language tag is required.");
        }

        var trimmed = value.Trim();

        if (!Supported.Contains(trimmed))
        {
            return Error.Validation(
                "Language.Unsupported",
                $"'{trimmed}' is not a supported language.");
        }

        return new LanguageTag(trimmed);
    }

    /// <summary>
    /// Best-effort resolution for an <c>Accept-Language</c> header, which is a hint from a
    /// device rather than a user's stated choice. Falls back rather than failing, because a
    /// browser sending <c>de-DE</c> should still get a usable page.
    /// </summary>
    public static LanguageTag FromHeaderOrDefault(string? value) =>
        Create(value) is { IsSuccess: true } result ? result.Value : Default;

    public override string ToString() => Value;
}
