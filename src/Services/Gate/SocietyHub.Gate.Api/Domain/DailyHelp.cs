using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Gate.Api.Domain;

/// <summary>What a domestic worker does. Drives the attendance sheet's grouping.</summary>
public enum HelpCategory
{
    Maid = 0,
    Cook = 1,
    Driver = 2,
    Nanny = 3,
    Cleaner = 4,
    Gardener = 5,
    Other = 6,
}

/// <summary>
/// A domestic worker with a standing arrangement at one or more flats.
///
/// Modelled as its own entity rather than as a repeating visit pass because the relationship
/// is ongoing and the useful output is a monthly sheet, not a series of admissions. A maid
/// working six flats is one person the gate recognises, not six visitors a day.
///
/// This is also the group the platform must be most careful with. They are the least powerful
/// people it touches and the ones with the least ability to object to being tracked, which is
/// why attendance is a QR or card punch and never a mandatory biometric.
/// </summary>
public sealed class DailyHelp : Entity, ITenantScoped, IAuditable
{
    private readonly List<HelpAssignment> _assignments = [];

    public DailyHelp(
        Guid id,
        Guid societyId,
        string fullName,
        string phoneNumber,
        HelpCategory category) : base(id)
    {
        SocietyId = societyId;
        FullName = fullName;
        PhoneNumber = phoneNumber;
        Category = category;
    }

    private DailyHelp()
    {
    }

    public Guid SocietyId { get; private set; }

    public string FullName { get; private set; } = string.Empty;

    /// <summary>E.164. Also how they receive their own attendance summary.</summary>
    public string PhoneNumber { get; private set; } = string.Empty;

    public HelpCategory Category { get; private set; }

    /// <summary>
    /// The card or QR badge they present at the gate. Deliberately a possession factor rather
    /// than a biometric — see the note on the type.
    /// </summary>
    public string? BadgeCode { get; set; }

    public string? PhotoBlobKey { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<HelpAssignment> Assignments => _assignments.AsReadOnly();

    public void AssignToFlat(Guid flatId)
    {
        if (_assignments.Any(a => a.FlatId == flatId && a.IsActive))
        {
            return;
        }

        _assignments.Add(new HelpAssignment(Guid.CreateVersion7(), SocietyId, Id, flatId));
    }
}

/// <summary>One flat a worker is engaged by. The join that makes a shared maid expressible.</summary>
public sealed class HelpAssignment : Entity, ITenantScoped
{
    public HelpAssignment(Guid id, Guid societyId, Guid dailyHelpId, Guid flatId) : base(id)
    {
        SocietyId = societyId;
        DailyHelpId = dailyHelpId;
        FlatId = flatId;
    }

    private HelpAssignment()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid DailyHelpId { get; private set; }

    public Guid FlatId { get; private set; }

    public bool IsActive { get; set; } = true;

    public DailyHelp? DailyHelp { get; set; }
}

/// <summary>
/// One worker's presence on one day.
///
/// A day is the unit rather than each punch, because the question a resident asks is "did she
/// come today", and the question at month end is "how many days". Storing punches loose would
/// make both a grouping query over the highest-volume table in the service.
/// </summary>
public sealed class AttendanceRecord : Entity, ITenantScoped
{
    public AttendanceRecord(
        Guid id,
        Guid societyId,
        Guid dailyHelpId,
        DateOnly workDate) : base(id)
    {
        SocietyId = societyId;
        DailyHelpId = dailyHelpId;
        WorkDate = workDate;
    }

    private AttendanceRecord()
    {
    }

    public Guid SocietyId { get; private set; }

    public Guid DailyHelpId { get; private set; }

    /// <summary>
    /// The society's local date, not UTC.
    ///
    /// A maid arriving at 05:30 IST is on 30 June UTC but working the 1 July shift. Storing
    /// the UTC date would put her first day of the month in the previous one, and the monthly
    /// sheet — which is what she is paid from — would be wrong.
    /// </summary>
    public DateOnly WorkDate { get; private set; }

    public DateTimeOffset? FirstInAtUtc { get; private set; }

    public DateTimeOffset? LastOutAtUtc { get; private set; }

    /// <summary>Counts re-entries, so a worker moving between flats is not double-counted.</summary>
    public int PunchCount { get; private set; }

    public bool IsPresent => FirstInAtUtc is not null;

    /// <summary>Minutes on site, when both ends of the day are known.</summary>
    public int? MinutesOnSite =>
        FirstInAtUtc is { } first && LastOutAtUtc is { } last
            ? (int)(last - first).TotalMinutes
            : null;

    public Result PunchIn(DateTimeOffset now)
    {
        PunchCount++;

        // Only the first entry of the day sets arrival. A worker leaving for the market and
        // returning has not started a second day.
        FirstInAtUtc ??= now;

        return Result.Success();
    }

    public Result PunchOut(DateTimeOffset now)
    {
        if (FirstInAtUtc is null)
        {
            return Error.Conflict("Attendance.NotPunchedIn", "No arrival recorded for today.");
        }

        // Always the latest departure, so the final exit is what the sheet reports.
        LastOutAtUtc = now;

        return Result.Success();
    }
}
