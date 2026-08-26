using System.Security.Cryptography;
using System.Text;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Identity.Api.Domain;

/// <summary>
/// A one-time code sent to a phone.
///
/// Phone OTP is the primary sign-in for residents — passwords are a poor fit when the
/// audience includes people who will use exactly one app on a shared family phone. That makes
/// this the most attacked surface in the platform, so the defences are deliberate:
///
/// the code is stored as a salted hash and never in plaintext, so a database read does not
/// yield a live credential; attempts are capped so the six-digit space cannot be walked; and
/// the window is short so an intercepted SMS has little value.
///
/// Not society-scoped: at request time we know only a phone number, and which societies it
/// belongs to is resolved after the code is verified.
/// </summary>
public sealed class OtpChallenge : Entity
{
    /// <summary>Six digits: the most a resident will read off an SMS without error.</summary>
    public const int CodeLength = 6;

    /// <summary>Long enough for a delayed SMS, short enough to limit an intercepted one.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Three tries, then the challenge is dead and a new code must be requested.
    ///
    /// Bounded attempts are what make six digits safe. One in a million per guess is
    /// meaningless if an attacker gets a million guesses.
    /// </summary>
    public const int MaxAttempts = 3;

    private OtpChallenge(Guid id, string phoneNumber, string codeHash, string salt, DateTimeOffset expiresAtUtc)
        : base(id)
    {
        PhoneNumber = phoneNumber;
        CodeHash = codeHash;
        Salt = salt;
        ExpiresAtUtc = expiresAtUtc;
    }

    private OtpChallenge()
    {
    }

    /// <summary>E.164, always with the country code.</summary>
    public string PhoneNumber { get; private set; } = string.Empty;

    public string CodeHash { get; private set; } = string.Empty;

    /// <summary>
    /// Per-challenge salt. Without it, identical codes hash identically and the table becomes
    /// a lookup: see the same hash twice and you know the code.
    /// </summary>
    public string Salt { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    /// <summary>Recorded so repeated failures from one source are visible to rate limiting.</summary>
    public string? RequestedFromIp { get; set; }

    public bool IsConsumed => ConsumedAtUtc is not null;

    public bool IsExhausted => AttemptCount >= MaxAttempts;

    public bool HasExpired(DateTimeOffset now) => now >= ExpiresAtUtc;

    public bool IsUsable(DateTimeOffset now) => !IsConsumed && !IsExhausted && !HasExpired(now);

    /// <summary>
    /// Issues a challenge and returns the plaintext code alongside it — the only moment the
    /// code exists in readable form. The caller sends it and must not persist it.
    /// </summary>
    public static (OtpChallenge Challenge, string Code) Issue(string phoneNumber, DateTimeOffset now)
    {
        // RandomNumberGenerator, not Random. A predictable OTP is not a one-time code.
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString($"D{CodeLength}");
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var challenge = new OtpChallenge(
            Guid.CreateVersion7(),
            phoneNumber,
            Hash(code, salt),
            salt,
            now.Add(Lifetime));

        return (challenge, code);
    }

    /// <summary>
    /// Checks a submitted code, counting the attempt whether or not it matches.
    /// </summary>
    public bool TryConsume(string submittedCode, DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            return false;
        }

        // Counted before comparing, so an attacker cannot avoid the cap by abandoning the
        // request mid-flight.
        AttemptCount++;

        var candidate = Hash(submittedCode, Salt);

        // Fixed-time comparison. A normal string compare returns faster on an early mismatch,
        // which over enough requests leaks the code one digit at a time.
        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate),
            Encoding.UTF8.GetBytes(CodeHash));

        if (matches)
        {
            ConsumedAtUtc = now;
        }

        return matches;
    }

    private static string Hash(string code, string salt) =>
        Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(salt + ':' + code)));
}
