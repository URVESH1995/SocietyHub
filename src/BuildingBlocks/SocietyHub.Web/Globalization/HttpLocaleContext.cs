using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Globalization;

namespace SocietyHub.Web.Globalization;

/// <summary>
/// Resolves language, time zone, currency and country for the current request.
///
/// Phase 1 reads the society's defaults from claims stamped at token issue. When the
/// Society service lands, these come from a Redis-cached society profile instead, so a
/// committee changing the society's language does not require every resident to log in
/// again for it to take effect.
/// </summary>
public sealed class HttpLocaleContext : ILocaleContext
{
    private const string PreferredLanguageClaim = "preferred_language";
    private const string SocietyLanguageClaim = "society_lang";
    private const string SocietyTimeZoneClaim = "society_tz";
    private const string SocietyCurrencyClaim = "society_currency";
    private const string SocietyCountryClaim = "society_country";

    private readonly IHttpContextAccessor _accessor;

    public HttpLocaleContext(IHttpContextAccessor accessor) => _accessor = accessor;

    /// <summary>
    /// Resolution order, strongest first:
    ///
    /// <list type="number">
    ///   <item>the resident's explicitly saved preference,</item>
    ///   <item>the device's <c>Accept-Language</c> header,</item>
    ///   <item>the society's configured default,</item>
    ///   <item>the platform default.</item>
    /// </list>
    ///
    /// A resident who chose Hindi keeps Hindi on a borrowed phone. The device header sits
    /// above the society default on purpose: a phone set to English is a real signal from
    /// that person, whereas the society default is a committee's guess about everyone.
    /// </summary>
    public LanguageTag Language
    {
        get
        {
            var context = _accessor.HttpContext;

            if (LanguageTag.Create(context?.User.FindFirst(PreferredLanguageClaim)?.Value)
                is { IsSuccess: true } residentChoice)
            {
                return residentChoice.Value;
            }

            if (context is not null
                && context.Request.Headers.TryGetValue("Accept-Language", out var header)
                && LanguageTag.Create(FirstAcceptedLanguage(header))
                    is { IsSuccess: true } fromDevice)
            {
                return fromDevice.Value;
            }

            if (LanguageTag.Create(context?.User.FindFirst(SocietyLanguageClaim)?.Value)
                is { IsSuccess: true } societyDefault)
            {
                return societyDefault.Value;
            }

            return LanguageTag.Default;
        }
    }

    public TimeZoneInfo TimeZone
    {
        get
        {
            var id = _accessor.HttpContext?.User.FindFirst(SocietyTimeZoneClaim)?.Value;

            if (string.IsNullOrWhiteSpace(id))
            {
                return IndiaStandardTime;
            }

            // FindSystemTimeZoneById accepts IANA ids on .NET 6+ across platforms, but a
            // society row holding a stale or misspelled zone must not 500 the request.
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return IndiaStandardTime;
            }
        }
    }

    public string Currency =>
        _accessor.HttpContext?.User.FindFirst(SocietyCurrencyClaim)?.Value is { Length: 3 } code
            ? code.ToUpperInvariant()
            : "INR";

    public string CountryCode =>
        _accessor.HttpContext?.User.FindFirst(SocietyCountryClaim)?.Value is { Length: 2 } code
            ? code.ToUpperInvariant()
            : "IN";

    private static TimeZoneInfo IndiaStandardTime
    {
        get
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.Utc;
            }
        }
    }

    /// <summary>
    /// Takes the first tag from an <c>Accept-Language</c> header, discarding q-weights.
    /// Full weighted negotiation is not worth it here: the header is only ever a fallback
    /// behind an explicit user preference.
    /// </summary>
    private static string? FirstAcceptedLanguage(StringValues header)
    {
        var raw = header.ToString();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var first = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .FirstOrDefault();

        return first?.Split(';', StringSplitOptions.TrimEntries).FirstOrDefault();
    }
}
