using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Drives.Api.Domain;

public enum EnrolmentStatus
{
    /// <summary>Joined, payment not yet confirmed by the gateway.</summary>
    Pending = 0,

    /// <summary>Money captured. Counts toward quorum and is owed a refund if the drive fails.</summary>
    Paid = 1,

    /// <summary>Left before cut-off. Owed a refund if they had paid.</summary>
    Withdrawn = 2,

    /// <summary>The drive failed and this participant is owed their money back.</summary>
    RefundDue = 3,

    /// <summary>Money returned, with the gateway's reference.</summary>
    Refunded = 4,

    /// <summary>
    /// Payment never completed — abandoned at the gateway, or declined. Distinct from
    /// Withdrawn because nothing was ever taken and nothing is owed.
    /// </summary>
    PaymentFailed = 5,
}

/// <summary>
/// One flat's place in a drive, and the money attached to it.
///
/// <para>
/// Two prices are kept deliberately. <see cref="UnitPriceAtJoinPaise"/> is what was charged on
/// the day, and <see cref="FinalUnitPricePaise"/> is what the drive settled at. The difference
/// is owed back, and keeping both is the only way to answer "why did I get ₹450 back" six weeks
/// later — a question somebody always asks.
/// </para>
/// </summary>
public sealed class DriveEnrolment : Entity, ITenantScoped
{
    private DriveEnrolment() { }

    public DriveEnrolment(
        Guid id,
        Guid societyId,
        Guid driveId,
        Guid userId,
        Guid flatId,
        int units,
        long unitPriceAtJoinPaise,
        DateTimeOffset joinedAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        DriveId = driveId;
        UserId = userId;
        FlatId = flatId;
        Units = units;
        UnitPriceAtJoinPaise = unitPriceAtJoinPaise;
        JoinedAtUtc = joinedAtUtc;
        Status = EnrolmentStatus.Pending;
    }

    public Guid SocietyId { get; private set; }

    public Guid DriveId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>
    /// The flat, and the reason quorum is counted per flat rather than per person. A service
    /// is delivered to a home, not to whoever happened to tap the button.
    /// </summary>
    public Guid FlatId { get; private set; }

    public int Units { get; private set; }

    public long UnitPriceAtJoinPaise { get; private set; }

    /// <summary>Set when the drive closes. Null while it is still open.</summary>
    public long? FinalUnitPricePaise { get; private set; }

    public EnrolmentStatus Status { get; private set; }

    /// <summary>The gateway's payment reference, for reconciliation and disputes.</summary>
    public string? PaymentReference { get; private set; }

    public string? RefundReference { get; private set; }

    public DateTimeOffset JoinedAtUtc { get; private set; }

    public DateTimeOffset? PaidAtUtc { get; private set; }

    public DateTimeOffset? SettledAtUtc { get; private set; }

    public DateTimeOffset? RefundedAtUtc { get; private set; }

    /// <summary>What was actually charged when they joined.</summary>
    public long AmountChargedPaise => UnitPriceAtJoinPaise * Units;

    /// <summary>What they should have paid at the drive's final price.</summary>
    public long AmountOwedPaise => (FinalUnitPricePaise ?? UnitPriceAtJoinPaise) * Units;

    /// <summary>
    /// The difference to return once the drive settles.
    ///
    /// Never negative. A drive whose price rose would mean charging people more after they
    /// committed, which the rate card's non-increasing invariant already prevents — but
    /// clamping here means a bug there cannot turn into an unexpected debit.
    /// </summary>
    public long RefundDuePaise => Math.Max(0, AmountChargedPaise - AmountOwedPaise);

    public void MarkPaid(string paymentReference, DateTimeOffset nowUtc)
    {
        PaymentReference = paymentReference;
        Status = EnrolmentStatus.Paid;
        PaidAtUtc = nowUtc;
    }

    public void MarkPaymentFailed() => Status = EnrolmentStatus.PaymentFailed;

    public void Withdraw(DateTimeOffset nowUtc)
    {
        // Someone who paid is owed their money; someone who never completed payment is not.
        // Collapsing these two into one state is how a refund gets issued for a charge that
        // never happened.
        Status = Status == EnrolmentStatus.Paid
            ? EnrolmentStatus.RefundDue
            : EnrolmentStatus.Withdrawn;

        SettledAtUtc = nowUtc;
    }

    /// <summary>
    /// Records the drive's final price against this enrolment.
    ///
    /// Where the partial refund is decided: an early joiner charged at the higher rate is owed
    /// the difference, and the amount is derived from the two stored prices rather than passed
    /// in, so no caller can get it wrong.
    /// </summary>
    public void SettleToFinalPrice(long finalUnitPricePaise, DateTimeOffset nowUtc)
    {
        FinalUnitPricePaise = finalUnitPricePaise;
        SettledAtUtc = nowUtc;
    }

    public void MarkRefundDue(DateTimeOffset nowUtc)
    {
        if (Status != EnrolmentStatus.Paid)
        {
            // Nothing was taken, so nothing is owed. Guarding here rather than at the call site
            // because the compensation loop iterates over enrolments and one mis-set state
            // would issue a refund against a payment that does not exist.
            return;
        }

        Status = EnrolmentStatus.RefundDue;
        SettledAtUtc = nowUtc;
    }

    public void MarkRefunded(string refundReference, DateTimeOffset nowUtc)
    {
        RefundReference = refundReference;
        Status = EnrolmentStatus.Refunded;
        RefundedAtUtc = nowUtc;
    }
}
