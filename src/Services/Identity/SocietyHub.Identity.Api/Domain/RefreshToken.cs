using System.Security.Cryptography;
using System.Text;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Identity.Api.Domain;

/// <summary>
/// A refresh token, belonging to a family that traces back to one sign-in.
///
/// Access tokens live ten minutes; refresh tokens live months, which makes a stolen one far
/// more valuable. Rotation plus reuse detection is the defence, and the family is what makes
/// detection possible.
///
/// Each use issues a new token and retires the old one. A retired token being presented again
/// means two parties hold the same credential — the legitimate client and a thief — and there
/// is no way to tell which is which. So the entire family is revoked: the real user signs in
/// again, and the attacker's stolen chain dies with it. Silent tolerance would leave the
/// thief with indefinite access.
/// </summary>
public sealed class RefreshToken : Entity
{
    /// <summary>Long enough that a resident is not signed out between uses of a seasonal app.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

    private RefreshToken(
        Guid id,
        Guid userId,
        Guid familyId,
        string tokenHash,
        Guid societyId,
        DateTimeOffset expiresAtUtc) : base(id)
    {
        UserId = userId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        SocietyId = societyId;
        ExpiresAtUtc = expiresAtUtc;
    }

    private RefreshToken()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>
    /// Shared by every token descended from one sign-in. Revoking the family is what turns a
    /// single detected reuse into a full eviction.
    /// </summary>
    public Guid FamilyId { get; private set; }

    /// <summary>
    /// Hashed, like a password. A database leak must not hand over live sessions.
    /// </summary>
    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>
    /// The society this session is scoped to. Switching societies mints a new token rather
    /// than mutating this one, so a token always answers for exactly one society.
    /// </summary>
    public Guid SocietyId { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? UsedAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevocationReason { get; private set; }

    /// <summary>The token issued when this one was used, for tracing a chain after an incident.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    /// <summary>Recorded to help a user recognise a session that is not theirs.</summary>
    public string? CreatedFromIp { get; set; }

    public string? UserAgent { get; set; }

    public bool IsUsed => UsedAtUtc is not null;

    public bool IsRevoked => RevokedAtUtc is not null;

    public bool IsActive(DateTimeOffset now) => !IsUsed && !IsRevoked && now < ExpiresAtUtc;

    /// <summary>Starts a new family. Called on sign-in.</summary>
    public static (RefreshToken Token, string PlainText) Issue(
        Guid userId,
        Guid societyId,
        DateTimeOffset now) =>
        Create(userId, Guid.CreateVersion7(), societyId, now);

    /// <summary>Continues an existing family. Called on rotation.</summary>
    public static (RefreshToken Token, string PlainText) IssueInFamily(
        Guid userId,
        Guid familyId,
        Guid societyId,
        DateTimeOffset now) =>
        Create(userId, familyId, societyId, now);

    private static (RefreshToken, string) Create(
        Guid userId,
        Guid familyId,
        Guid societyId,
        DateTimeOffset now)
    {
        // 256 bits of entropy. This is a bearer credential valid for two months; it must not
        // be guessable, and a GUID's 122 bits with structure is not the right tool.
        var plainText = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var token = new RefreshToken(
            Guid.CreateVersion7(),
            userId,
            familyId,
            Hash(plainText),
            societyId,
            now.Add(Lifetime));

        return (token, plainText);
    }

    public void MarkUsed(DateTimeOffset now, Guid replacedByTokenId)
    {
        UsedAtUtc = now;
        ReplacedByTokenId = replacedByTokenId;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAtUtc = now;
        RevocationReason = reason;
    }

    /// <summary>
    /// Plain SHA-256, not a password hash.
    ///
    /// Deliberate: the input is 256 random bits, not a human-chosen secret, so there is no
    /// dictionary to attack and nothing for a slow KDF to defend against. Argon2 here would
    /// add latency to every token refresh for no security gain.
    /// </summary>
    public static string Hash(string plainText) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plainText)));
}
