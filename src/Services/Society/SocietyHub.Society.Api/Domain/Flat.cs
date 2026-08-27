using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Society.Api.Domain;

/// <summary>A block or wing. Groups flats and gives the gate a coarse location.</summary>
public sealed class Tower : Entity, ITenantScoped
{
    private readonly List<Flat> _flats = [];

    public Tower(Guid id, Guid societyId, string name) : base(id)
    {
        SocietyId = societyId;
        Name = name;
    }

    private Tower()
    {
    }

    public Guid SocietyId { get; private set; }

    /// <summary>"A", "Tower 3", "Orchid Wing" — whatever the society already calls it.</summary>
    public string Name { get; private set; } = string.Empty;

    public int? FloorCount { get; set; }

    public IReadOnlyCollection<Flat> Flats => _flats.AsReadOnly();

    public Flat AddFlat(string flatNumber, int floorNumber, string flatType)
    {
        if (_flats.Any(f => string.Equals(f.FlatNumber, flatNumber, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Flat '{flatNumber}' already exists in this tower.");
        }

        var flat = new Flat(Guid.CreateVersion7(), SocietyId, Id, flatNumber, floorNumber, flatType);
        _flats.Add(flat);

        return flat;
    }
}

/// <summary>How a flat is currently lived in.</summary>
public enum Occupancy
{
    Vacant = 0,
    OwnerOccupied = 1,
    Rented = 2,
}

/// <summary>
/// A home. The unit almost everything else in the platform hangs off — a visitor is expected
/// at a flat, a complaint is raised by one, a bulk drive is bought per flat.
///
/// Floor is an integer rather than its own entity. A floor has no identity, no behaviour and
/// nothing referring to it; modelling it as a table would add a join to every flat query in
/// return for nothing.
/// </summary>
public sealed class Flat : AggregateRoot, ITenantScoped, IAuditable
{
    private readonly List<Resident> _residents = [];

    public Flat(
        Guid id,
        Guid societyId,
        Guid towerId,
        string flatNumber,
        int floorNumber,
        string flatType) : base(id)
    {
        SocietyId = societyId;
        TowerId = towerId;
        FlatNumber = flatNumber;
        FloorNumber = floorNumber;
        FlatType = flatType;
    }

    private Flat()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid TowerId { get; private set; }

    /// <summary>"A-101", "1204". Unique within its tower, not across the society.</summary>
    public string FlatNumber { get; private set; } = string.Empty;

    public int FloorNumber { get; private set; }

    /// <summary>"1BHK", "2BHK", "3BHK", "Penthouse". Free text — layouts vary too much.</summary>
    public string FlatType { get; private set; } = string.Empty;

    public decimal? CarpetAreaSqFt { get; set; }

    public Occupancy Occupancy { get; private set; } = Occupancy.Vacant;

    public IReadOnlyCollection<Resident> Residents => _residents.AsReadOnly();

    public Tower? Tower { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    /// <summary>
    /// Adds a resident and recomputes occupancy from who now lives here.
    /// </summary>
    public Resident AddResident(
        Guid userId,
        Relationship relationship,
        DateTimeOffset now,
        bool isPrimaryContact = false)
    {
        if (_residents.Any(r => r.UserId == userId && r.IsActive))
        {
            throw new InvalidOperationException("That person already lives in this flat.");
        }

        // Exactly one primary contact. They are who the gate calls when a visitor arrives, so
        // "several" and "none" are both broken answers.
        if (isPrimaryContact)
        {
            foreach (var existing in _residents.Where(r => r.IsActive))
            {
                existing.ClearPrimaryContact();
            }
        }

        var resident = new Resident(
            Guid.CreateVersion7(), SocietyId, Id, userId, relationship, now)
        {
            IsPrimaryContact = isPrimaryContact || _residents.All(r => !r.IsActive),
        };

        _residents.Add(resident);
        RecalculateOccupancy();

        return resident;
    }

    public void RemoveResident(Guid residentId, DateTimeOffset now)
    {
        var resident = _residents.SingleOrDefault(r => r.Id == residentId && r.IsActive)
            ?? throw new InvalidOperationException("No such active resident in this flat.");

        // Captured before the move-out, which clears the flag. Reading it afterwards would
        // always see false and silently skip the promotion below, leaving an occupied flat
        // with nobody for the gate to call.
        var wasPrimaryContact = resident.IsPrimaryContact;

        resident.MoveOut(now);
        RecalculateOccupancy();

        // The flat must not be left without a contact while anyone still lives in it.
        if (wasPrimaryContact)
        {
            var successor = _residents
                .Where(r => r.IsActive)
                .OrderByDescending(r => r.Relationship == Relationship.Owner)
                .ThenBy(r => r.MovedInAtUtc)
                .FirstOrDefault();

            successor?.MakePrimaryContact();
        }
    }

    /// <summary>
    /// Occupancy is derived, never set directly. An owner living here means owner-occupied; a
    /// tenant means rented; nobody means vacant. Storing it independently would let it drift
    /// from the residents it is supposed to describe.
    /// </summary>
    private void RecalculateOccupancy()
    {
        var active = _residents.Where(r => r.IsActive).ToList();

        Occupancy = active.Count switch
        {
            0 => Occupancy.Vacant,
            _ when active.Any(r => r.Relationship == Relationship.Tenant) => Occupancy.Rented,
            _ => Occupancy.OwnerOccupied,
        };
    }
}
