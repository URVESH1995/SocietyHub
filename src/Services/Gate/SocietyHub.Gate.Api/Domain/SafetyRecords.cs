using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Gate.Api.Domain;

/// <summary>
/// A person the committee has flagged.
///
/// A match raises an alert for the guard to verify and refuses a *pass*, but it never
/// physically bars anyone — the platform has no authority to do that, and acting
/// automatically on a list a neighbour can add to would be indefensible.
///
/// Every entry therefore carries who added it and why, and a review date. A blacklist nobody
/// revisits becomes a permanent accusation with no appeal, which is how these turn into
/// instruments of a personal dispute rather than security.
/// </summary>
public sealed class BlacklistEntry : Entity, ITenantScoped, IAuditable
{
    public BlacklistEntry(
        Guid id,
        Guid societyId,
        string phoneNumber,
        string reason,
        Guid addedByUserId,
        DateTimeOffset reviewDueAtUtc) : base(id)
    {
        SocietyId = societyId;
        PhoneNumber = phoneNumber;
        Reason = reason;
        AddedByUserId = addedByUserId;
        ReviewDueAtUtc = reviewDueAtUtc;
    }

    private BlacklistEntry()
    {
    }

    public Guid SocietyId { get; private set; }

    /// <summary>
    /// Matched on phone rather than name. Names collide constantly in a society of a thousand
    /// people, and flagging the wrong Sharma is a real harm.
    /// </summary>
    public string PhoneNumber { get; private set; } = string.Empty;

    public string? PersonName { get; set; }

    /// <summary>Mandatory. An entry with no stated reason cannot be reviewed or appealed.</summary>
    public string Reason { get; private set; } = string.Empty;

    public Guid AddedByUserId { get; private set; }

    /// <summary>Forces a periodic decision to keep or drop the entry.</summary>
    public DateTimeOffset ReviewDueAtUtc { get; private set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset? LiftedAtUtc { get; private set; }

    public string? LiftedReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public bool NeedsReview(DateTimeOffset now) => IsActive && now >= ReviewDueAtUtc;

    public void Lift(string reason, DateTimeOffset now)
    {
        IsActive = false;
        LiftedAtUtc = now;
        LiftedReason = reason;
    }
}

public enum SosCategory
{
    Medical = 0,
    Fire = 1,
    Security = 2,
    Other = 3,
}

public enum SosStatus
{
    Raised = 0,
    Acknowledged = 1,
    Resolved = 2,

    /// <summary>Raised by accident. Recorded, never deleted.</summary>
    FalseAlarm = 3,
}

/// <summary>
/// A panic alert.
///
/// The highest-priority thing this service produces, and the only one with a hard latency
/// target — five seconds end to end. It rides the Critical message lane so it can never queue
/// behind a notice broadcast.
///
/// A false alarm is resolved, not deleted. Someone who triggers one by accident and finds no
/// trace of it has no reason to trust that a real one would have been recorded either.
/// </summary>
public sealed class SosIncident : AggregateRoot, ITenantScoped
{
    public SosIncident(
        Guid id,
        Guid societyId,
        Guid raisedByUserId,
        Guid? flatId,
        SosCategory category,
        DateTimeOffset raisedAtUtc) : base(id)
    {
        SocietyId = societyId;
        RaisedByUserId = raisedByUserId;
        FlatId = flatId;
        Category = category;
        RaisedAtUtc = raisedAtUtc;
        Status = SosStatus.Raised;
    }

    private SosIncident()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid RaisedByUserId { get; private set; }

    public Guid? FlatId { get; private set; }

    public SosCategory Category { get; private set; }

    public SosStatus Status { get; private set; }

    public DateTimeOffset RaisedAtUtc { get; private set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    public Guid? AcknowledgedByUserId { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public string? ResolutionNotes { get; private set; }

    /// <summary>
    /// Time to first human acknowledgement. The number that actually says whether the alert
    /// worked — everything else is process.
    /// </summary>
    public TimeSpan? TimeToAcknowledge =>
        AcknowledgedAtUtc is { } ack ? ack - RaisedAtUtc : null;

    public Result Acknowledge(Guid userId, DateTimeOffset now)
    {
        if (Status != SosStatus.Raised)
        {
            return Error.Conflict("Sos.AlreadyHandled", "That alert has already been picked up.");
        }

        Status = SosStatus.Acknowledged;
        AcknowledgedAtUtc = now;
        AcknowledgedByUserId = userId;

        return Result.Success();
    }

    public Result Resolve(string notes, bool wasFalseAlarm, DateTimeOffset now)
    {
        if (Status is SosStatus.Resolved or SosStatus.FalseAlarm)
        {
            return Error.Conflict("Sos.AlreadyClosed", "That alert is already closed.");
        }

        Status = wasFalseAlarm ? SosStatus.FalseAlarm : SosStatus.Resolved;
        ResolvedAtUtc = now;
        ResolutionNotes = notes;

        return Result.Success();
    }
}
