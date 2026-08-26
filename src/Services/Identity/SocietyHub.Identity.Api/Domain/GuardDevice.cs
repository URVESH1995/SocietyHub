using System.Security.Cryptography;
using System.Text;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Identity.Api.Domain;

/// <summary>
/// A tablet at a gate, and the shift PIN that unlocks it.
///
/// Guard devices break the assumptions the resident app is built on. The tablet is shared,
/// bolted to a desk, used by whoever is on shift, and periodically stolen. A long-lived token
/// on it would be a permanent key to the building.
///
/// So the device is registered as its own identity and the human is authenticated separately
/// by a shift PIN. Losing the tablet means revoking one row, and every gate entry is still
/// attributable to the guard who was signed in rather than merely to "the gate".
/// </summary>
public sealed class GuardDevice : Entity, ITenantScoped, IAuditable
{
    /// <summary>
    /// Short, because a guard types it dozens of times a shift on a touchscreen. It is a
    /// second factor on a physically controlled device, not a standalone secret — which is
    /// why the attempt cap below is what actually protects it.
    /// </summary>
    public const int PinLength = 6;

    public const int MaxPinAttempts = 5;

    /// <summary>A device token outlives a shift but not a posting.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    public GuardDevice(Guid id, Guid societyId, string deviceIdentifier, string displayName)
        : base(id)
    {
        SocietyId = societyId;
        DeviceIdentifier = deviceIdentifier;
        DisplayName = displayName;
    }

    private GuardDevice()
    {
    }

    public Guid SocietyId { get; private set; }

    /// <summary>Hardware identifier reported at registration, for recognising a re-enrolment.</summary>
    public string DeviceIdentifier { get; private set; } = string.Empty;

    /// <summary>Where it is, e.g. "Main Gate — Tower A". Shown in the audit log.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    public string? PinHash { get; private set; }

    public string? PinSalt { get; private set; }

    public int FailedPinAttempts { get; private set; }

    public DateTimeOffset? LockedUntilUtc { get; private set; }

    /// <summary>The guard currently signed in, so entries are attributable to a person.</summary>
    public Guid? ActiveGuardUserId { get; private set; }

    public DateTimeOffset? ShiftStartedAtUtc { get; private set; }

    /// <summary>
    /// Kills the device immediately. The single action taken when a tablet is reported
    /// stolen, and the reason device identity is separate from the guard's.
    /// </summary>
    public bool IsRevoked { get; private set; }

    public DateTimeOffset? LastSeenAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public bool IsLockedOut(DateTimeOffset now) => LockedUntilUtc is { } until && now < until;

    public void SetShiftPin(string pin, DateTimeOffset now)
    {
        PinSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        PinHash = HashPin(pin, PinSalt);
        FailedPinAttempts = 0;
        LockedUntilUtc = null;
        ModifiedAtUtc = now;
    }

    /// <summary>
    /// Verifies the shift PIN and starts a shift for <paramref name="guardUserId"/>.
    /// </summary>
    public bool TryStartShift(string pin, Guid guardUserId, DateTimeOffset now)
    {
        if (IsRevoked || IsLockedOut(now) || PinHash is null || PinSalt is null)
        {
            return false;
        }

        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashPin(pin, PinSalt)),
            Encoding.UTF8.GetBytes(PinHash));

        if (!matches)
        {
            FailedPinAttempts++;

            // Lockout rather than a slow-down. Six digits on a device an attacker is holding
            // falls quickly to brute force; the cap is the only thing that makes it a factor.
            if (FailedPinAttempts >= MaxPinAttempts)
            {
                LockedUntilUtc = now.AddMinutes(15);
            }

            return false;
        }

        FailedPinAttempts = 0;
        LockedUntilUtc = null;
        ActiveGuardUserId = guardUserId;
        ShiftStartedAtUtc = now;

        return true;
    }

    public void EndShift()
    {
        ActiveGuardUserId = null;
        ShiftStartedAtUtc = null;
    }

    public void Revoke() => IsRevoked = true;

    private static string HashPin(string pin, string salt) =>
        Convert.ToBase64String(
            Rfc2898DeriveBytes.Pbkdf2(
                password: Encoding.UTF8.GetBytes(pin),
                salt: Convert.FromBase64String(salt),
                // A six-digit PIN has a million possibilities, so unlike the refresh token
                // this genuinely is a weak human secret and deserves a slow KDF offline.
                iterations: 100_000,
                hashAlgorithm: HashAlgorithmName.SHA256,
                outputLength: 32));
}
