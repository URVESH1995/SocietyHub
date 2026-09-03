using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Scheduling.Api.Domain;

/// <summary>
/// A window on the service day that residents pick from.
///
/// <para>
/// Windows, not appointments. A technician cannot promise 10:15 when the flat before them may
/// have a seized compressor, and a platform that pretends otherwise generates a complaint for
/// every job that runs long. "Morning, 9am to 1pm" is a promise that can be kept.
/// </para>
///
/// <para>
/// Capacity is the number of jobs, derived from the technicians assigned rather than typed in.
/// A slot whose capacity exceeds what its technicians can physically do is a slot that
/// overbooks, and the people it disappoints have already paid.
/// </para>
/// </summary>
public sealed class ServiceSlot : AggregateRoot, ITenantScoped, IAuditable
{
    private readonly List<SlotTechnician> _technicians = [];

    private ServiceSlot() { }

    public ServiceSlot(
        Guid id,
        Guid societyId,
        Guid driveId,
        DateOnly serviceDate,
        TimeOnly startsAt,
        TimeOnly endsAt,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        DriveId = driveId;
        ServiceDate = serviceDate;
        StartsAt = startsAt;
        EndsAt = endsAt;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public Guid DriveId { get; private set; }

    /// <summary>
    /// Held as a date in the society's local calendar, not an instant.
    ///
    /// A service day is a day where the flats are, and converting it through UTC is how a
    /// 9am slot in India becomes the previous evening in the database and then shows up on the
    /// wrong day in a list sorted by timestamp.
    /// </summary>
    public DateOnly ServiceDate { get; private set; }

    public TimeOnly StartsAt { get; private set; }

    public TimeOnly EndsAt { get; private set; }

    public int BookedCount { get; private set; }

    public bool IsCancelled { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<SlotTechnician> Technicians => _technicians;

    /// <summary>
    /// Jobs this slot can take, from the people actually assigned to it.
    ///
    /// Derived rather than stored, so removing a technician cannot leave a stale capacity
    /// behind — which would let the slot keep accepting bookings nobody can service.
    /// </summary>
    public int Capacity => _technicians.Sum(t => t.JobsInThisSlot);

    public int PlacesLeft => Math.Max(0, Capacity - BookedCount);

    public bool CanTake(int jobs) => !IsCancelled && PlacesLeft >= jobs;

    public Result AssignTechnician(
        Guid technicianId, string technicianName, int jobsInThisSlot, DateTimeOffset nowUtc)
    {
        if (IsCancelled)
        {
            return Error.Conflict(
                "slot.cancelled", "A cancelled slot cannot have technicians assigned.");
        }

        if (jobsInThisSlot < 1)
        {
            return Error.Validation(
                "slot.bad_capacity", "A technician assigned to a slot must take at least one job.");
        }

        if (_technicians.Any(t => t.TechnicianId == technicianId))
        {
            return Error.Conflict(
                "slot.already_assigned", "This technician is already on this slot.");
        }

        _technicians.Add(new SlotTechnician(
            Guid.CreateVersion7(), SocietyId, Id, technicianId, technicianName, jobsInThisSlot));

        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result RemoveTechnician(Guid technicianId, DateTimeOffset nowUtc)
    {
        var assignment = _technicians.FirstOrDefault(t => t.TechnicianId == technicianId);

        if (assignment is null)
        {
            return Error.NotFound("slot.not_assigned", "That technician is not on this slot.");
        }

        // Removing capacity below what is already booked would silently oversell the slot.
        // Refusing forces whoever is reshuffling a rota to move the jobs first, which is the
        // conversation that has to happen anyway.
        if (Capacity - assignment.JobsInThisSlot < BookedCount)
        {
            return Error.Conflict(
                "slot.would_oversell",
                $"{BookedCount} jobs are booked. Move them before removing this technician.");
        }

        _technicians.Remove(assignment);
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result Book(int jobs, DateTimeOffset nowUtc)
    {
        if (!CanTake(jobs))
        {
            return Error.Conflict(
                "slot.full", "This slot has no room left. Please choose another.");
        }

        BookedCount += jobs;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public void Release(int jobs, DateTimeOffset nowUtc)
    {
        // Clamped at zero. A double release would otherwise drive the count negative and make
        // the slot appear to have more capacity than it has.
        BookedCount = Math.Max(0, BookedCount - jobs);
        ModifiedAtUtc = nowUtc;
    }

    public Result Cancel(DateTimeOffset nowUtc)
    {
        if (BookedCount > 0)
        {
            return Error.Conflict(
                "slot.has_bookings",
                $"{BookedCount} jobs are booked into this slot. Reschedule them first.");
        }

        IsCancelled = true;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }
}

/// <summary>
/// One technician's share of a slot.
///
/// The name is copied rather than looked up. A resident's job card has to say who is coming
/// even when the vendor service is unreachable, and a technician who later leaves the vendor
/// must still appear on the record of a job they did.
/// </summary>
public sealed class SlotTechnician : Entity, ITenantScoped
{
    private SlotTechnician() { }

    public SlotTechnician(
        Guid id,
        Guid societyId,
        Guid slotId,
        Guid technicianId,
        string technicianName,
        int jobsInThisSlot)
        : base(id)
    {
        SocietyId = societyId;
        SlotId = slotId;
        TechnicianId = technicianId;
        TechnicianName = technicianName;
        JobsInThisSlot = jobsInThisSlot;
    }

    public Guid SocietyId { get; private set; }

    public Guid SlotId { get; private set; }

    public Guid TechnicianId { get; private set; }

    public string TechnicianName { get; private set; } = string.Empty;

    public int JobsInThisSlot { get; private set; }
}
