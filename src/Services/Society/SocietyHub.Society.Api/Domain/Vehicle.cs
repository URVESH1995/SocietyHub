using System.Text.RegularExpressions;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Society.Api.Domain;

public enum VehicleType
{
    Car = 0,
    Motorcycle = 1,
    Bicycle = 2,
    Other = 3,
}

/// <summary>
/// A vehicle registered to a flat.
///
/// The registration is stored normalised — uppercase, no spaces or dashes — because it is
/// matched against what an ANPR camera reads in Phase 3, and no two people write a plate the
/// same way. "MH 12 AB 1234", "MH-12-AB-1234" and "mh12ab1234" are one vehicle, and deciding
/// that at write time is far cheaper than at every read.
/// </summary>
public sealed partial class Vehicle : Entity, ITenantScoped, IAuditable
{
    public Vehicle(
        Guid id,
        Guid societyId,
        Guid flatId,
        string registrationNumber,
        VehicleType type) : base(id)
    {
        SocietyId = societyId;
        FlatId = flatId;
        RegistrationNumber = Normalise(registrationNumber);
        Type = type;
    }

    private Vehicle()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid FlatId { get; private set; }

    /// <summary>Normalised. Use <see cref="Normalise"/> before comparing anything to this.</summary>
    public string RegistrationNumber { get; private set; } = string.Empty;

    public VehicleType Type { get; private set; }

    public string? Make { get; set; }

    public string? Model { get; set; }

    public string? Colour { get; set; }

    /// <summary>The slot this vehicle usually occupies, where one is allotted.</summary>
    public Guid? ParkingSlotId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    /// <summary>Strips everything that is not a letter or digit and uppercases the rest.</summary>
    public static string Normalise(string registration) =>
        NonAlphanumericRegex().Replace(registration ?? string.Empty, string.Empty).ToUpperInvariant();

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumericRegex();
}

public enum ParkingSlotType
{
    Covered = 0,
    Open = 1,
    Stilt = 2,
    Visitor = 3,
    Disabled = 4,
    ElectricVehicle = 5,
}

/// <summary>
/// A parking space.
///
/// Kept separate from <see cref="Vehicle"/> because slots outlive the cars in them and are
/// allotted to <em>flats</em>, not people — a slot stays with A-101 when its tenant changes,
/// which is exactly how societies actually allocate them.
/// </summary>
public sealed class ParkingSlot : Entity, ITenantScoped, IAuditable
{
    public ParkingSlot(Guid id, Guid societyId, string slotNumber, ParkingSlotType type) : base(id)
    {
        SocietyId = societyId;
        SlotNumber = slotNumber;
        Type = type;
    }

    private ParkingSlot()
    {
    }

    public Guid SocietyId { get; private set; }

    /// <summary>"B1-045", "Stilt-12".</summary>
    public string SlotNumber { get; private set; } = string.Empty;

    public ParkingSlotType Type { get; private set; }

    /// <summary>Null for visitor bays and unallotted slots.</summary>
    public Guid? AllottedToFlatId { get; private set; }

    public string? Level { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public void AllotTo(Guid flatId)
    {
        if (Type is ParkingSlotType.Visitor)
        {
            // Allotting the visitor bay to a flat is how a society ends up with nowhere for
            // visitors to park and a standing argument at the gate.
            throw new InvalidOperationException("A visitor slot cannot be allotted to a flat.");
        }

        AllottedToFlatId = flatId;
    }

    public void Release() => AllottedToFlatId = null;
}
