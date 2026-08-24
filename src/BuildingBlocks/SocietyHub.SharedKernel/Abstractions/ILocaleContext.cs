using SocietyHub.SharedKernel.Globalization;

namespace SocietyHub.SharedKernel.Abstractions;

/// <summary>
/// The locale to render this request in, resolved once per request.
///
/// Resolution order is deliberate: the resident's saved preference beats the device's
/// <c>Accept-Language</c> header, which beats the society's default, which beats the
/// platform default. A resident who chose Tamil keeps Tamil on a borrowed phone.
/// </summary>
public interface ILocaleContext
{
    LanguageTag Language { get; }

    /// <summary>
    /// The society's IANA time zone, e.g. <c>Asia/Kolkata</c>.
    ///
    /// Everything is stored in UTC, but the 24-hour complaint SLA, escalation windows and
    /// "do not disturb" notification hours are all judged against society-local time. A
    /// complaint raised at 11pm is not the same promise as one raised at 9am.
    /// </summary>
    TimeZoneInfo TimeZone { get; }

    /// <summary>ISO 4217 code the society transacts in.</summary>
    string Currency { get; }

    /// <summary>ISO 3166-1 alpha-2, driving SMS routing and data-residency rules.</summary>
    string CountryCode { get; }
}
