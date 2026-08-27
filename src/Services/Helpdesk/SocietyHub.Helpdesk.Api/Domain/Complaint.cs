using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Helpdesk.Api.Domain;

/// <summary>
/// A resident's report of something broken, and the society's promise to fix it.
///
/// The ticket is the whole point of the service. A complaint in a WhatsApp group of 300 people
/// has no owner, no deadline and no record; the same complaint here has all three, and the
/// escalation ladder means nobody has to remember to chase it.
/// </summary>
public sealed class Complaint : AggregateRoot, ITenantScoped, IAuditable
{
    private readonly List<ComplaintNote> _notes = [];
    private readonly List<ComplaintAttachment> _attachments = [];

    public Complaint(
        Guid id,
        Guid societyId,
        Guid flatId,
        Guid raisedByUserId,
        string ticketNumber,
        ComplaintCategory category,
        ComplaintPriority priority,
        string title,
        string description,
        DateTimeOffset raisedAtUtc,
        DateTimeOffset slaDueAtUtc) : base(id)
    {
        SocietyId = societyId;
        FlatId = flatId;
        RaisedByUserId = raisedByUserId;
        TicketNumber = ticketNumber;
        Category = category;
        Priority = priority;
        Title = title;
        Description = description;
        RaisedAtUtc = raisedAtUtc;
        SlaDueAtUtc = slaDueAtUtc;
        Status = ComplaintStatus.Open;
    }

    private Complaint()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid FlatId { get; private set; }

    public Guid RaisedByUserId { get; private set; }

    /// <summary>
    /// Human-readable, per society, per year: <c>CMP-2026-00412</c>.
    ///
    /// Residents quote this to a guard or a committee member out loud. A GUID is unusable for
    /// that, which is why it exists alongside the id rather than instead of it.
    /// </summary>
    public string TicketNumber { get; private set; } = string.Empty;

    public ComplaintCategory Category { get; private set; }

    public ComplaintPriority Priority { get; private set; }

    public ComplaintStatus Status { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public DateTimeOffset RaisedAtUtc { get; private set; }

    /// <summary>Computed once from working hours at creation. Never recalculated silently.</summary>
    public DateTimeOffset SlaDueAtUtc { get; private set; }

    public Guid? AssignedToUserId { get; private set; }

    public string? AssignedToName { get; private set; }

    public DateTimeOffset? AssignedAtUtc { get; private set; }

    public DateTimeOffset? FirstResponseAtUtc { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    public string? Resolution { get; private set; }

    /// <summary>
    /// How far up the ladder this has gone. 0 = nobody notified beyond the assignee.
    ///
    /// Stored rather than derived, so the sweeper can tell a first breach from a second and
    /// escalate one rung at a time instead of re-alerting the same people every pass.
    /// </summary>
    public int EscalationLevel { get; private set; }

    public DateTimeOffset? LastEscalatedAtUtc { get; private set; }

    /// <summary>One to five, given by the resident on close. Null if they never rated.</summary>
    public int? SatisfactionRating { get; private set; }

    public string? RatingComment { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<ComplaintNote> Notes => _notes.AsReadOnly();

    public IReadOnlyCollection<ComplaintAttachment> Attachments => _attachments.AsReadOnly();

    public bool IsOpen => Status is not (ComplaintStatus.Closed or ComplaintStatus.Rejected);

    /// <summary>
    /// Breach is judged against resolution, not closure.
    ///
    /// Closure waits on the resident to confirm, and they may be travelling. Holding the
    /// society to a deadline that depends on a resident replying would make the metric
    /// measure the wrong party.
    /// </summary>
    public bool HasBreachedSla(DateTimeOffset now) =>
        ResolvedAtUtc is null ? now > SlaDueAtUtc : ResolvedAtUtc > SlaDueAtUtc;

    public TimeSpan? TimeToResolve =>
        ResolvedAtUtc is { } resolved ? resolved - RaisedAtUtc : null;

    public Result Assign(Guid assigneeId, string assigneeName, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Conflict("Complaint.Closed", "That complaint is already closed.");
        }

        AssignedToUserId = assigneeId;
        AssignedToName = assigneeName;
        AssignedAtUtc = now;

        if (Status == ComplaintStatus.Open)
        {
            Status = ComplaintStatus.Assigned;
        }

        return Result.Success();
    }

    public Result Start(DateTimeOffset now)
    {
        if (Status is not (ComplaintStatus.Open or ComplaintStatus.Assigned))
        {
            return Error.Conflict("Complaint.NotStartable", "That complaint is not waiting to start.");
        }

        Status = ComplaintStatus.InProgress;
        FirstResponseAtUtc ??= now;

        return Result.Success();
    }

    public Result Resolve(string resolution, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Conflict("Complaint.Closed", "That complaint is already closed.");
        }

        if (string.IsNullOrWhiteSpace(resolution))
        {
            // "Fixed" with no detail gives the resident nothing to verify against, and gives
            // the next person to hit the same fault nothing to learn from.
            return Error.Validation(
                "Complaint.ResolutionRequired", "Describe what was done to fix it.");
        }

        Status = ComplaintStatus.Resolved;
        Resolution = resolution;
        ResolvedAtUtc = now;
        FirstResponseAtUtc ??= now;

        return Result.Success();
    }

    /// <summary>
    /// The resident confirms and optionally rates.
    ///
    /// Only they may close it. A society that could close its own tickets would report
    /// perfect SLA compliance and fix nothing.
    /// </summary>
    public Result Close(int? rating, string? comment, DateTimeOffset now)
    {
        if (Status == ComplaintStatus.Closed)
        {
            return Error.Conflict("Complaint.AlreadyClosed", "That complaint is already closed.");
        }

        if (Status != ComplaintStatus.Resolved)
        {
            return Error.Conflict("Complaint.NotResolved", "That complaint has not been resolved yet.");
        }

        if (rating is not null && rating is < 1 or > 5)
        {
            return Error.Validation("Complaint.BadRating", "A rating must be between 1 and 5.");
        }

        Status = ComplaintStatus.Closed;
        ClosedAtUtc = now;
        SatisfactionRating = rating;
        RatingComment = comment;

        return Result.Success();
    }

    /// <summary>
    /// The resident says it is not actually fixed. Returns the ticket to the assignee.
    ///
    /// The SLA deadline is deliberately not reset. A reopened complaint is already late;
    /// extending its deadline would let a premature "resolved" buy a fresh window, which is
    /// exactly the gaming the metric exists to prevent.
    /// </summary>
    public Result Reopen(string reason, DateTimeOffset now)
    {
        if (Status != ComplaintStatus.Resolved)
        {
            return Error.Conflict("Complaint.NotResolved", "Only a resolved complaint can be reopened.");
        }

        Status = ComplaintStatus.InProgress;
        ResolvedAtUtc = null;
        Resolution = null;

        _notes.Add(new ComplaintNote(
            Guid.CreateVersion7(), SocietyId, Id, RaisedByUserId, $"Reopened: {reason}", now, false));

        return Result.Success();
    }

    public Result Reject(string reason, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Conflict("Complaint.Closed", "That complaint is already closed.");
        }

        Status = ComplaintStatus.Rejected;
        Resolution = reason;
        ClosedAtUtc = now;

        return Result.Success();
    }

    /// <summary>
    /// Moves one rung up the ladder. Called only by the sweeper.
    ///
    /// Returns the new level so the caller can decide who to notify — the escalation matrix
    /// is a delivery concern and does not belong inside the aggregate.
    /// </summary>
    public int Escalate(DateTimeOffset now)
    {
        EscalationLevel++;
        LastEscalatedAtUtc = now;
        return EscalationLevel;
    }

    public void AddNote(Guid authorId, string body, DateTimeOffset now, bool internalOnly) =>
        _notes.Add(new ComplaintNote(
            Guid.CreateVersion7(), SocietyId, Id, authorId, body, now, internalOnly));

    public void AddAttachment(string blobKey, string fileName, string contentType, long sizeBytes) =>
        _attachments.Add(new ComplaintAttachment(
            Guid.CreateVersion7(), SocietyId, Id, blobKey, fileName, contentType, sizeBytes));
}

/// <summary>
/// A comment on a complaint.
///
/// <see cref="IsInternalOnly"/> separates what maintenance staff say to each other from what
/// the resident sees. Without it, either the resident reads "third time this month, the
/// plumber is useless", or staff stop writing anything useful down.
/// </summary>
public sealed class ComplaintNote : Entity, ITenantScoped
{
    public ComplaintNote(
        Guid id,
        Guid societyId,
        Guid complaintId,
        Guid authorUserId,
        string body,
        DateTimeOffset createdAtUtc,
        bool isInternalOnly) : base(id)
    {
        SocietyId = societyId;
        ComplaintId = complaintId;
        AuthorUserId = authorUserId;
        Body = body;
        CreatedAtUtc = createdAtUtc;
        IsInternalOnly = isInternalOnly;
    }

    private ComplaintNote()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid ComplaintId { get; private set; }

    public Guid AuthorUserId { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public bool IsInternalOnly { get; private set; }
}

/// <summary>
/// A photo or document on a complaint. The blob key is society-prefixed for the same reason
/// gate photos are: blob storage has no tenant filter of its own.
/// </summary>
public sealed class ComplaintAttachment : Entity, ITenantScoped
{
    /// <summary>Generous enough for a phone photo, bounded so a video cannot be uploaded.</summary>
    public const long MaxSizeBytes = 8 * 1024 * 1024;

    public ComplaintAttachment(
        Guid id,
        Guid societyId,
        Guid complaintId,
        string blobKey,
        string fileName,
        string contentType,
        long sizeBytes) : base(id)
    {
        SocietyId = societyId;
        ComplaintId = complaintId;
        BlobKey = blobKey;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
    }

    private ComplaintAttachment()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid ComplaintId { get; private set; }

    public string BlobKey { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }
}
