using System.Text.RegularExpressions;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.SharedKernel.Globalization;

/// <summary>
/// A phone number in E.164 form, always including the country code: <c>+919876543210</c>.
///
/// The tempting shortcut is a ten-digit Indian number, and it is the single most expensive
/// schema mistake this platform could make. Phone number is the login identity, the OTP
/// destination and the visitor's only identifier — so the day the first society outside
/// India signs up, a bare ten-digit column requires backfilling every one of those, in
/// every service, with no reliable way to infer the country after the fact.
/// </summary>
public readonly partial record struct PhoneNumber
{
    private PhoneNumber(string value) => Value = value;

    public string Value { get; }

    /// <summary>The country calling code, e.g. <c>91</c>. Useful for SMS provider routing.</summary>
    public string CountryCode => CountryCodeRegex().Match(Value) is { Success: true } m
        ? m.Groups[1].Value
        : string.Empty;

    public static Result<PhoneNumber> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation("Phone.Empty", "A phone number is required.");
        }

        // Tolerate what people actually type: spaces, dashes, brackets.
        var cleaned = SeparatorRegex().Replace(value.Trim(), string.Empty);

        if (!E164Regex().IsMatch(cleaned))
        {
            return Error.Validation(
                "Phone.Invalid",
                "Phone number must be in international format, for example +919876543210.");
        }

        return new PhoneNumber(cleaned);
    }

    /// <summary>
    /// Accepts a national number and attaches a country code. For the Indian apps this is
    /// what turns a resident typing <c>9876543210</c> into a storable identity.
    /// </summary>
    public static Result<PhoneNumber> CreateNational(string? value, string countryCallingCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation("Phone.Empty", "A phone number is required.");
        }

        var cleaned = SeparatorRegex().Replace(value.Trim(), string.Empty);

        return cleaned.StartsWith('+')
            ? Create(cleaned)
            : Create($"+{countryCallingCode.TrimStart('+')}{cleaned.TrimStart('0')}");
    }

    /// <summary>Last four digits only, for display in logs and support tooling.</summary>
    public string ToMasked() => Value.Length <= 4
        ? new string('*', Value.Length)
        : string.Concat(new string('*', Value.Length - 4), Value.AsSpan(Value.Length - 4));

    public override string ToString() => Value;

    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex E164Regex();

    [GeneratedRegex(@"[\s\-().]")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"^\+(\d{1,3})")]
    private static partial Regex CountryCodeRegex();
}
