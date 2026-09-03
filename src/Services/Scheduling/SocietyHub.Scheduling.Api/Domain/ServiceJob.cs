using System.Security.Cryptography;
using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Scheduling.Api.Domain;

public enum JobStatus
{
    /// <summary>Booked into a slot. The resident has a window and a technician's name.</summary>
    Scheduled = 0,

    /// <summary>Technician is on the way. Sent so a resident knows to be in.</summary>
    EnRoute = 1,

    /// <summary>Technician is in the flat and working.</summary>
    InProgress = 2,

    /// <summary>Done, with the resident's confirmation.</summary>
    Completed = 3,

    /// <summary>Cancelled before it happened, by either side.</summary>
    Cancelled = 4,

    /// <summary>
    /// The technician did not turn up in the window. Counted against the vendor separately
    /// from a cancellation, because a no-show wastes a resident's morning.
    /// </summary>
    NoShow = 5,

    /// <summary>
    /// The technician arrived and could not get in. Recorded distinctly because the vendor
    /// travelled and the resident was out, and neither side should absorb it silently.
    /// </summary>
    ResidentUnavailable = 6,
}

/// <summary>
/// One flat's service, on one day, by one technician.
///
/// <para>
/// The unit the resident actually experiences. A drive is a commercial arrangement; this is
/// somebody in their kitchen at 10am.
/// </para>
///
/// <para>
/// <b>Completion is proved by the resident, not claimed by the technician.</b> A four-digit
/// code is generated when the job is scheduled, shown only to the resident, and given to the
/// technician at the door when the work is done. Without it a vendor can mark sixty jobs
/// complete from a van, and the first anyone knows is a wave of complaints two weeks later
/// against a payout that has already gone out.
/// </para>
/// </summary>
public sealed class ServiceJob : AggregateRoot, ITenantScoped, IAuditable
{
    private ServiceJob() { }

    public ServiceJob(
        Guid id,
        Guid societyId,
        Guid driveId,
        Guid enrolmentId,
        Guid slotId,
        Guid residentUserId,
        Guid flatId,
        int units,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        DriveId = driveId;
        EnrolmentId = enrolmentId;
        SlotId = slotId;
        ResidentUserId = residentUserId;
        FlatId = flatId;
        Units = units;
        Status = JobStatus.Scheduled;
        CompletionCode = GenerateCompletionCode();
        CreatedAtUtc = createdAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public Guid DriveId { get; private set; }

    /// <summary>Ties the job to the money. One enrolment, one job, one payment.</summary>
    public Guid EnrolmentId { get; private set; }

    public Guid SlotId { get; private set; }

    public Guid ResidentUserId { get; private set; }

    public Guid FlatId { get; private set; }

    public int Units { get; private set; }

    public JobStatus Status { get; private set; }

    public Guid? TechnicianId { get; private set; }

    public string? TechnicianName { get; private set; }

    /// <summary>
    /// Four digits the resident gives the technician at the door.
    ///
    /// Not a secret worth hashing — it is shown in the resident's app and read aloud in a
    /// doorway, and it protects against a vendor marking work complete that was never done,
    /// not against an attacker. Hashing it would only stop the platform showing it to the
    /// resident, which is the whole mechanism.
    /// </summary>
    public string CompletionCode { get; private set; } = string.Empty;

    public int CompletionAttempts { get; private set; }

    public DateTimeOffset? EnRouteAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>Blob key for the technician's photo of the finished work, where one applies.</summary>
    public string? ProofPhotoKey { get; private set; }

    public string? TechnicianNotes { get; private set; }

    public int? ResidentRating { get; private set; }

    public string? ResidentComment { get; private set; }

    public string? CancellationReason { get; private set; }

    public int RescheduleCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public bool IsTerminal =>
        Status is JobStatus.Completed or JobStatus.Cancelled
            or JobStatus.NoShow or JobStatus.ResidentUnavailable;

    public Result AssignTechnician(Guid technicianId, string name, DateTimeOffset nowUtc)
    {
        if (IsTerminal)
        {
            return Error.Conflict(
                "job.finished", "This job has already finished and cannot be reassigned.");
        }

        TechnicianId = technicianId;
        TechnicianName = name;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result MarkEnRoute(DateTimeOffset nowUtc)
    {
        if (Status is not JobStatus.Scheduled)
        {
            return Error.Conflict(
                "job.not_scheduled", "Only a scheduled job can be marked en route.");
        }

        Status = JobStatus.EnRoute;
        EnRouteAtUtc = nowUtc;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result Start(DateTimeOffset nowUtc)
    {
        if (Status is not (JobStatus.Scheduled or JobStatus.EnRoute))
        {
            return Error.Conflict("job.not_startable", "This job cannot be started.");
        }

        Status = JobStatus.InProgress;
        StartedAtUtc = nowUtc;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Completes the job against the resident's code.
    ///
    /// The attempt cap is not about brute force — four digits guessed by someone standing in
    /// the flat is not the threat. It is about a technician who cannot get the code because the
    /// resident has gone out, trying repeatedly, and the job silently sitting in progress. Past
    /// the cap the vendor has to raise it as unavailable, which is a real outcome with a real
    /// consequence rather than a job stuck forever.
    /// </summary>
    public Result CompleteWithCode(
        string code, string? proofPhotoKey, string? notes, DateTimeOffset nowUtc)
    {
        if (Status is not (JobStatus.InProgress or JobStatus.EnRoute))
        {
            return Error.Conflict(
                "job.not_in_progress", "Only a job in progress can be completed.");
        }

        if (CompletionAttempts >= 5)
        {
            return Error.Conflict(
                "job.too_many_attempts",
                "Too many wrong codes. Mark the resident unavailable and contact the office.");
        }

        // Fixed-time comparison out of habit rather than necessity. It costs nothing, and the
        // next code that gets compared this way may well be a real secret.
        var matches = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(CompletionCode),
            System.Text.Encoding.UTF8.GetBytes(code.Trim()));

        if (!matches)
        {
            CompletionAttempts++;
            ModifiedAtUtc = nowUtc;

            return Error.Validation(
                "job.wrong_code", "That code does not match. Ask the resident to check.");
        }

        Status = JobStatus.Completed;
        CompletedAtUtc = nowUtc;
        ProofPhotoKey = proofPhotoKey;
        TechnicianNotes = notes;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Rated afterwards, separately from completion.
    ///
    /// Asking for a rating at the door, in front of the technician, produces fives. The number
    /// only means anything if it is given later and privately, which is why this is its own
    /// operation rather than a field on completion.
    /// </summary>
    public Result Rate(int rating, string? comment, DateTimeOffset nowUtc)
    {
        if (Status is not JobStatus.Completed)
        {
            return Error.Conflict("job.not_completed", "Only a completed job can be rated.");
        }

        if (rating is < 1 or > 5)
        {
            return Error.Validation("job.bad_rating", "A rating is between one and five.");
        }

        ResidentRating = rating;
        ResidentComment = comment;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Moves the job to another slot.
    ///
    /// The count is kept because a job rescheduled four times is a signal — either the vendor
    /// is over-committed or the resident is unreachable — and neither shows up in a status
    /// field that only records where the job is now.
    /// </summary>
    public Result RescheduleTo(Guid slotId, DateTimeOffset nowUtc)
    {
        if (IsTerminal)
        {
            return Error.Conflict("job.finished", "A finished job cannot be rescheduled.");
        }

        if (slotId == SlotId)
        {
            return Error.Validation(
                "job.same_slot", "That is the slot this job is already in.");
        }

        SlotId = slotId;
        RescheduleCount++;

        // Back to Scheduled: a technician who was en route to the old slot is no longer on
        // their way, and leaving the status would tell the resident somebody is coming.
        Status = JobStatus.Scheduled;
        EnRouteAtUtc = null;
        StartedAtUtc = null;
        TechnicianId = null;
        TechnicianName = null;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset nowUtc)
    {
        if (Status is JobStatus.Completed)
        {
            return Error.Conflict(
                "job.completed", "A completed job cannot be cancelled. Raise a complaint.");
        }

        Status = JobStatus.Cancelled;
        CancellationReason = reason;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result MarkNoShow(DateTimeOffset nowUtc)
    {
        if (IsTerminal)
        {
            return Error.Conflict("job.finished", "This job has already finished.");
        }

        Status = JobStatus.NoShow;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result MarkResidentUnavailable(string? notes, DateTimeOffset nowUtc)
    {
        if (Status is not (JobStatus.EnRoute or JobStatus.InProgress or JobStatus.Scheduled))
        {
            return Error.Conflict("job.not_active", "This job is not active.");
        }

        Status = JobStatus.ResidentUnavailable;
        TechnicianNotes = notes;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Four digits, from a cryptographic source.
    ///
    /// <c>Random</c> would be adequate for the threat and is avoided anyway: codes generated
    /// in a tight loop when a drive of sixty is scheduled would come from seeds microseconds
    /// apart, and a predictable set is one a vendor could work out.
    /// </summary>
    private static string GenerateCompletionCode() =>
        RandomNumberGenerator.GetInt32(1000, 10000).ToString();
}
