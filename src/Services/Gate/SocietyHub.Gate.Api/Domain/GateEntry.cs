using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Gate.Api.Domain;

public enum EntryDirection
{
    Inbound = 0,
    Outbound = 1,
}

/// <summary>
/// One movement through the gate.
///
/// Deliberately append-only and separate from <see cref="VisitPass"/>. The pass is current
/// state — "is this visitor inside" — and gets mutated. This is the historical record, and it
/// is evidence: it answers who entered the building on the night of an incident, and a police
/// request months later has to be answerable from it.
///
/// It is also the highest-volume table in the platform. Around 210,000 rows a day at full
/// scale, roughly 77 million a year, which is why it carries a <see cref="PartitionKey"/> and
/// why nothing joins to it on the hot path.
///
/// Soft-deletable, so a society administrator cannot erase the record of who came in. Purging
/// is a separate, audited retention job.
/// </summary>
public sealed class GateEntry : Entity, ITenantScoped, ISoftDeletable
{
    public GateEntry(
        Guid id,
        Guid societyId,
        EntryDirection direction,
        DateTimeOffset occurredAtUtc) : base(id)
    {
        SocietyId = societyId;
        Direction = direction;
        OccurredAtUtc = occurredAtUtc;
        PartitionKey = ToPartitionKey(occurredAtUtc);
    }

    private GateEntry()
    {
    }

    public Guid SocietyId { get; private set; }

    public EntryDirection Direction { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>
    /// Year and month as <c>yyyyMM</c>, stamped at creation.
    ///
    /// SQL Server partitions on a column, not an expression, so deriving the month at query
    /// time would defeat partition elimination entirely. Storing it costs four bytes and is
    /// what lets a query for last week read one partition instead of seventy-seven million
    /// rows — and what lets an old month be switched out to cold storage as a metadata
    /// operation rather than a delete that runs for hours.
    /// </summary>
    public int PartitionKey { get; private set; }

    /// <summary>Set when the movement was authorised by a pass. Null for a walk-up.</summary>
    public Guid? VisitPassId { get; private set; }

    /// <summary>Set for resident and staff movements, which carry no pass.</summary>
    public Guid? DailyHelpId { get; set; }

    public Guid? FlatId { get; set; }

    public string? PersonName { get; set; }

    public string? PersonPhone { get; set; }

    public VisitorType? VisitorType { get; set; }

    public string? VehicleNumber { get; set; }

    public string? PhotoBlobKey { get; set; }

    /// <summary>The guard on duty. Every entry is attributable to a person, not to "the gate".</summary>
    public Guid? RecordedByGuardId { get; set; }

    public Guid? RecordedOnDeviceId { get; set; }

    /// <summary>
    /// True when the entry was captured offline and synced later.
    ///
    /// Worth recording rather than hiding: it explains why a row arrived hours after it
    /// happened, and stops an investigator reading the sync time as the entry time.
    /// </summary>
    public bool WasOfflineCapture { get; set; }

    /// <summary>Courier left a parcel without entering. Common enough to model explicitly.</summary>
    public bool LeftAtGate { get; set; }

    public string? Notes { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public Guid? DeletedByUserId { get; set; }

    public static int ToPartitionKey(DateTimeOffset moment) =>
        (moment.UtcDateTime.Year * 100) + moment.UtcDateTime.Month;

    public static GateEntry ForPass(
        VisitPass pass,
        EntryDirection direction,
        Guid guardId,
        DateTimeOffset now) =>
        new(Guid.CreateVersion7(), pass.SocietyId, direction, now)
        {
            VisitPassId = pass.Id,
            FlatId = pass.FlatId,
            PersonName = pass.VisitorName,
            PersonPhone = pass.VisitorPhone,
            VisitorType = pass.VisitorType,
            VehicleNumber = pass.VehicleNumber,
            PhotoBlobKey = pass.PhotoBlobKey,
            RecordedByGuardId = guardId,
        };
}
