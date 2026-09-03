using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Drives.Api.Domain;

public enum DriveStatus
{
    /// <summary>Being set up by a committee. Invisible to residents.</summary>
    Draft = 0,

    /// <summary>Accepting enrolments. Money is taken as people join.</summary>
    Open = 1,

    /// <summary>Cut-off passed, quorum met, work being arranged with the vendor.</summary>
    Confirming = 2,

    /// <summary>Vendor engaged and jobs scheduled.</summary>
    Confirmed = 3,

    /// <summary>Every job done.</summary>
    Completed = 4,

    /// <summary>
    /// Cut-off passed without quorum. Everyone who paid is being refunded, and the drive stays
    /// in this state until every one of them has been.
    /// </summary>
    Refunding = 5,

    /// <summary>Ended with no work done and nobody out of pocket.</summary>
    Cancelled = 6,
}

/// <summary>
/// A group purchase: one society, one service, a window to join, and a minimum that makes it
/// worth a vendor's trip.
///
/// <para>
/// This is the aggregate the whole platform's commercial case rests on, and the one that
/// touches money. Two decisions shape everything else:
/// </para>
///
/// <para>
/// <b>Payment is taken at enrolment, not at quorum.</b> The alternative — collect commitments
/// and charge once quorum is met — sounds safer and produces drives that reach quorum on paper
/// and collapse when the charges go out. People who have paid stay. The cost is that a missed
/// quorum means real refunds, real gateway fees and a compensation path that has to work
/// perfectly, which is why it is built alongside the happy path rather than after it.
/// </para>
///
/// <para>
/// <b>The price falls as people join, and everybody pays the final price.</b> An early joiner
/// is not punished for joining early — they are refunded the difference. Anything else makes
/// the rational move "wait and see", and a drive where everyone waits never opens.
/// </para>
/// </summary>
public sealed class ServiceDrive : AggregateRoot, ITenantScoped, IAuditable
{
    private readonly List<DriveEnrolment> _enrolments = [];

    private ServiceDrive() { }

    public ServiceDrive(
        Guid id,
        Guid societyId,
        string serviceCode,
        Guid vendorId,
        Guid rateCardId,
        Guid openedByUserId,
        int quorum,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        ServiceCode = serviceCode;
        VendorId = vendorId;
        RateCardId = rateCardId;
        OpenedByUserId = openedByUserId;
        Quorum = quorum;
        Status = DriveStatus.Draft;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public string ServiceCode { get; private set; } = string.Empty;

    public Guid VendorId { get; private set; }

    /// <summary>
    /// Pinned at open, not looked up at charge time.
    ///
    /// A vendor editing their rate card mid-drive must not change what residents already agreed
    /// to pay. This is the difference between a price and a quote.
    /// </summary>
    public Guid RateCardId { get; private set; }

    public Guid OpenedByUserId { get; private set; }

    public DriveStatus Status { get; private set; }

    /// <summary>Participants needed for the drive to go ahead.</summary>
    public int Quorum { get; private set; }

    /// <summary>
    /// A ceiling, where the vendor has one. Null means unlimited.
    ///
    /// Real: a vendor with four technicians cannot service ninety flats on one Saturday, and a
    /// drive that oversells is a drive that disappoints people who already paid.
    /// </summary>
    public int? Capacity { get; private set; }

    public DateTimeOffset? OpensAtUtc { get; private set; }

    /// <summary>When enrolment closes and quorum is judged.</summary>
    public DateTimeOffset? CutOffAtUtc { get; private set; }

    /// <summary>When the work is expected to happen. Shown before anyone commits money.</summary>
    public DateTimeOffset? ServiceDateUtc { get; private set; }

    /// <summary>
    /// The unit price everyone ends up paying, fixed when the drive closes.
    ///
    /// Null while open, because it is not knowable until the final count is in. A screen that
    /// shows a live figure is showing the price <em>at the current count</em>, which the
    /// enrolment records already carry.
    /// </summary>
    public long? FinalUnitPricePaise { get; private set; }

    public string? CancellationReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<DriveEnrolment> Enrolments => _enrolments;

    /// <summary>Participants who are still in — cancelled ones do not count toward quorum.</summary>
    public int ActiveParticipantCount =>
        _enrolments.Count(e => e.Status is EnrolmentStatus.Paid or EnrolmentStatus.Pending);

    /// <summary>
    /// Billable units, which is not the same as participants.
    ///
    /// A flat with three ACs is one participant and three units. Slab thresholds are measured
    /// in units because that is what the vendor's cost scales with; quorum is measured in
    /// participants because that is what makes the trip worthwhile.
    /// </summary>
    public int ActiveUnitCount =>
        _enrolments
            .Where(e => e.Status is EnrolmentStatus.Paid or EnrolmentStatus.Pending)
            .Sum(e => e.Units);

    public bool HasReachedQuorum => ActiveParticipantCount >= Quorum;

    public bool IsFull => Capacity is not null && ActiveParticipantCount >= Capacity;

    public Result Open(
        DateTimeOffset nowUtc,
        DateTimeOffset cutOffAtUtc,
        DateTimeOffset serviceDateUtc,
        int? capacity)
    {
        if (Status is not DriveStatus.Draft)
        {
            return Error.Conflict("drive.not_draft", "Only a draft drive can be opened.");
        }

        if (cutOffAtUtc <= nowUtc)
        {
            return Error.Validation(
                "drive.cutoff_in_past", "A drive cannot close before it opens.");
        }

        if (serviceDateUtc <= cutOffAtUtc)
        {
            // The vendor needs time between knowing the final count and turning up. A service
            // date on the cut-off day means somebody is arranging technicians overnight.
            return Error.Validation(
                "drive.service_before_cutoff",
                "The service date must be after enrolment closes.");
        }

        if (Quorum < 1)
        {
            return Error.Validation("drive.bad_quorum", "Quorum must be at least one.");
        }

        if (capacity is not null && capacity < Quorum)
        {
            // A drive that cannot physically reach its own quorum is one that will always
            // refund, and it would take a cut-off period to discover that.
            return Error.Validation(
                "drive.capacity_below_quorum",
                "Capacity cannot be lower than the quorum the drive needs.");
        }

        Status = DriveStatus.Open;
        OpensAtUtc = nowUtc;
        CutOffAtUtc = cutOffAtUtc;
        ServiceDateUtc = serviceDateUtc;
        Capacity = capacity;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Adds a participant at the price applying to the count they joined at.
    ///
    /// The recorded price is provisional. Everyone is settled to the final price when the drive
    /// closes — see <see cref="CloseWithQuorum"/> — so this figure exists to show a resident
    /// what they were charged today, not what they will end up paying.
    /// </summary>
    public Result<DriveEnrolment> Enrol(
        Guid userId,
        Guid flatId,
        int units,
        long unitPriceAtJoinPaise,
        DateTimeOffset nowUtc)
    {
        if (Status is not DriveStatus.Open)
        {
            return Error.Conflict("drive.not_open", "This drive is not accepting enrolments.");
        }

        if (CutOffAtUtc is not null && nowUtc >= CutOffAtUtc)
        {
            return Error.Conflict("drive.closed", "Enrolment for this drive has closed.");
        }

        if (units < 1)
        {
            return Error.Validation("drive.bad_units", "Enrol at least one unit.");
        }

        // One enrolment per flat, not per person. Two people in the same household enrolling
        // separately would be charged twice for one service, and the vendor would arrive
        // expecting to do the work once.
        if (_enrolments.Any(e =>
                e.FlatId == flatId && e.Status is EnrolmentStatus.Paid or EnrolmentStatus.Pending))
        {
            return Error.Conflict(
                "drive.already_enrolled", "This flat has already joined this drive.");
        }

        if (IsFull)
        {
            return Error.Conflict(
                "drive.full", "This drive has reached the vendor's capacity.");
        }

        var enrolment = new DriveEnrolment(
            Guid.CreateVersion7(), SocietyId, Id, userId, flatId, units,
            unitPriceAtJoinPaise, nowUtc);

        _enrolments.Add(enrolment);
        ModifiedAtUtc = nowUtc;

        return enrolment;
    }

    /// <summary>
    /// A participant pulls out before cut-off.
    ///
    /// Allowed freely while the drive is open, and refunded in full. Making it hard would
    /// improve the quorum numbers and destroy the trust the whole feature runs on — a resident
    /// who cannot leave a drive will not join the next one.
    /// </summary>
    public Result Withdraw(Guid flatId, DateTimeOffset nowUtc)
    {
        if (Status is not DriveStatus.Open)
        {
            return Error.Conflict(
                "drive.not_open",
                "This drive has closed. Cancellation is handled with the vendor.");
        }

        var enrolment = _enrolments.FirstOrDefault(e =>
            e.FlatId == flatId && e.Status is EnrolmentStatus.Paid or EnrolmentStatus.Pending);

        if (enrolment is null)
        {
            return Error.NotFound("drive.not_enrolled", "This flat has not joined this drive.");
        }

        enrolment.Withdraw(nowUtc);
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Closes a drive that made it, fixing the final price for everyone.
    ///
    /// <paramref name="finalUnitPricePaise"/> is resolved by the caller from the pinned rate
    /// card at the final unit count. It is passed in rather than computed here because the rate
    /// card lives in another service, and reaching across a service boundary from inside an
    /// aggregate is how a domain model stops being testable.
    /// </summary>
    public Result CloseWithQuorum(long finalUnitPricePaise, DateTimeOffset nowUtc)
    {
        if (Status is not DriveStatus.Open)
        {
            return Error.Conflict("drive.not_open", "Only an open drive can be closed.");
        }

        if (!HasReachedQuorum)
        {
            return Error.Conflict(
                "drive.quorum_not_reached",
                $"{ActiveParticipantCount} of {Quorum} participants. Cancel instead.");
        }

        FinalUnitPricePaise = finalUnitPricePaise;
        Status = DriveStatus.Confirming;
        ModifiedAtUtc = nowUtc;

        // Everyone settles to the same price. An early joiner who paid the higher rate is owed
        // the difference — and paying it back is what makes joining early safe, which is what
        // gets a drive to quorum at all.
        foreach (var enrolment in _enrolments.Where(e => e.Status == EnrolmentStatus.Paid))
        {
            enrolment.SettleToFinalPrice(finalUnitPricePaise, nowUtc);
        }

        return Result.Success();
    }

    /// <summary>
    /// Closes a drive that did not make it. Everybody who paid is owed their money back.
    ///
    /// Moves to Refunding rather than straight to Cancelled, because the refunds are real calls
    /// to a payment gateway that fail and retry. Cancelled means every one of them succeeded.
    /// </summary>
    public Result CloseWithoutQuorum(string reason, DateTimeOffset nowUtc)
    {
        if (Status is not DriveStatus.Open)
        {
            return Error.Conflict("drive.not_open", "Only an open drive can be cancelled.");
        }

        CancellationReason = reason;
        ModifiedAtUtc = nowUtc;

        var owed = _enrolments.Where(e => e.Status == EnrolmentStatus.Paid).ToList();

        // Nobody paid, so there is nothing to compensate. Skipping Refunding here matters: a
        // drive stuck in Refunding with no refunds to make would never leave it.
        Status = owed.Count == 0 ? DriveStatus.Cancelled : DriveStatus.Refunding;

        foreach (var enrolment in owed)
        {
            enrolment.MarkRefundDue(nowUtc);
        }

        return Result.Success();
    }

    /// <summary>
    /// Called as each refund lands. The drive is Cancelled only once none are outstanding.
    ///
    /// Tracked per participant rather than as a single flag, because a crash part-way through
    /// sixty refunds must resume where it stopped. A boolean would either refund everyone twice
    /// or leave the remainder stranded, and both are discovered by a resident.
    /// </summary>
    public Result RecordRefund(Guid enrolmentId, string providerRefundId, DateTimeOffset nowUtc)
    {
        var enrolment = _enrolments.FirstOrDefault(e => e.Id == enrolmentId);

        if (enrolment is null)
        {
            return Error.NotFound("drive.enrolment_not_found", "No such enrolment.");
        }

        enrolment.MarkRefunded(providerRefundId, nowUtc);

        if (Status == DriveStatus.Refunding
            && _enrolments.All(e => e.Status != EnrolmentStatus.RefundDue))
        {
            Status = DriveStatus.Cancelled;
        }

        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result MarkConfirmed(DateTimeOffset nowUtc)
    {
        if (Status is not DriveStatus.Confirming)
        {
            return Error.Conflict(
                "drive.not_confirming", "Only a drive awaiting scheduling can be confirmed.");
        }

        Status = DriveStatus.Confirmed;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    public Result MarkCompleted(DateTimeOffset nowUtc)
    {
        if (Status is not DriveStatus.Confirmed)
        {
            return Error.Conflict("drive.not_confirmed", "Only a confirmed drive can complete.");
        }

        Status = DriveStatus.Completed;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>Enrolments still owed money, for the compensation loop to work through.</summary>
    public IReadOnlyList<DriveEnrolment> OutstandingRefunds =>
        [.. _enrolments.Where(e => e.Status == EnrolmentStatus.RefundDue)];
}
