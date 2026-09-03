using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Payments.Api.Domain;

public enum OrderStatus
{
    /// <summary>Created, gateway order placed, nothing charged.</summary>
    Created = 0,

    /// <summary>Money captured.</summary>
    Paid = 1,

    /// <summary>The gateway declined it, or the payer abandoned the page.</summary>
    Failed = 2,

    /// <summary>Some of it returned — the difference an early drive joiner is owed.</summary>
    PartiallyRefunded = 3,

    /// <summary>All of it returned.</summary>
    Refunded = 4,
}

/// <summary>
/// One payment: what was asked for, what was captured, and what has gone back.
///
/// <para>
/// <b>Amounts are paise as integers and are never recomputed.</b> Every figure here is a fact
/// about something that happened at a payment gateway, and a value that can be derived a second
/// time is a value that can be derived differently. The ledger below is what reconciliation
/// reads; these fields are its running totals.
/// </para>
///
/// <para>
/// The aggregate is deliberately dull. Money services fail in proportion to their cleverness,
/// and every interesting decision in this platform — quorum, slab pricing, who is owed what —
/// belongs to Drives. This one records.
/// </para>
/// </summary>
public sealed class PaymentOrder : AggregateRoot, ITenantScoped, IAuditable
{
    private readonly List<LedgerEntry> _ledger = [];

    private PaymentOrder() { }

    public PaymentOrder(
        Guid id,
        Guid societyId,
        Guid userId,
        string purpose,
        Guid referenceId,
        long amountPaise,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        UserId = userId;
        Purpose = purpose;
        ReferenceId = referenceId;
        AmountPaise = amountPaise;
        Status = OrderStatus.Created;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>What this pays for: <c>drive_enrolment</c> today, more later.</summary>
    public string Purpose { get; private set; } = string.Empty;

    /// <summary>
    /// The thing being paid for — a drive enrolment id.
    ///
    /// Unique per purpose, and that uniqueness is what makes every operation here idempotent.
    /// A retried enrolment finds the existing order rather than creating a second charge.
    /// </summary>
    public Guid ReferenceId { get; private set; }

    public long AmountPaise { get; private set; }

    public long RefundedPaise { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>The gateway's order id, created before the payer sees a payment page.</summary>
    public string? GatewayOrderId { get; private set; }

    /// <summary>The gateway's payment id, which only exists once money moves.</summary>
    public string? GatewayPaymentId { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset? PaidAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<LedgerEntry> Ledger => _ledger;

    /// <summary>What the platform is still holding.</summary>
    public long NetPaise => AmountPaise - RefundedPaise;

    public long RefundablePaise => Math.Max(0, AmountPaise - RefundedPaise);

    public void AttachGatewayOrder(string gatewayOrderId, DateTimeOffset nowUtc)
    {
        GatewayOrderId = gatewayOrderId;
        ModifiedAtUtc = nowUtc;
    }

    /// <summary>
    /// Records a capture.
    ///
    /// Idempotent on the gateway payment id, because this is reached from both the client's
    /// success callback and the gateway's webhook — deliberately, since either can be lost.
    /// Whichever arrives second must be inert, or the ledger double-counts and every
    /// reconciliation after it is wrong.
    /// </summary>
    public Result MarkPaid(string gatewayPaymentId, long capturedPaise, DateTimeOffset nowUtc)
    {
        if (Status is OrderStatus.Paid or OrderStatus.PartiallyRefunded or OrderStatus.Refunded)
        {
            return string.Equals(GatewayPaymentId, gatewayPaymentId, StringComparison.Ordinal)
                ? Result.Success()
                : Error.Conflict(
                    "payment.already_paid",
                    "This order was already paid by a different payment.");
        }

        if (capturedPaise != AmountPaise)
        {
            // A capture for the wrong amount is never a rounding difference — the amount was
            // sent to the gateway when the order was created. It means the order was tampered
            // with or the webhook belongs to a different order, and both must stop here rather
            // than be reconciled away later.
            return Error.Conflict(
                "payment.amount_mismatch",
                $"Captured {capturedPaise} paise against an order for {AmountPaise}.");
        }

        GatewayPaymentId = gatewayPaymentId;
        Status = OrderStatus.Paid;
        PaidAtUtc = nowUtc;
        ModifiedAtUtc = nowUtc;

        _ledger.Add(LedgerEntry.Capture(
            Guid.CreateVersion7(), SocietyId, Id, capturedPaise, gatewayPaymentId, nowUtc));

        return Result.Success();
    }

    public Result MarkFailed(string reason, DateTimeOffset nowUtc)
    {
        if (Status is OrderStatus.Paid)
        {
            // A failure notice arriving after a capture is a late or duplicated webhook, not a
            // reversal. Acting on it would mark a paid order failed and hand somebody a free
            // service.
            return Error.Conflict(
                "payment.already_paid", "This order was already paid.");
        }

        Status = OrderStatus.Failed;
        FailureReason = reason;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// Records a refund, whole or partial.
    ///
    /// Idempotent on the gateway's refund id for the same reason as capture: the compensation
    /// loop re-derives outstanding refunds on every pass and will legitimately ask twice after
    /// a lost response.
    /// </summary>
    public Result RecordRefund(
        string gatewayRefundId, long amountPaise, string reason, DateTimeOffset nowUtc)
    {
        if (_ledger.Any(e =>
                e.Kind == LedgerEntryKind.Refund
                && string.Equals(e.GatewayReference, gatewayRefundId, StringComparison.Ordinal)))
        {
            return Result.Success();
        }

        if (Status is OrderStatus.Created or OrderStatus.Failed)
        {
            return Error.Conflict(
                "payment.not_paid", "Nothing was captured, so there is nothing to refund.");
        }

        if (amountPaise <= 0)
        {
            return Error.Validation("payment.bad_refund", "A refund must be more than zero.");
        }

        if (amountPaise > RefundablePaise)
        {
            // Refunding beyond what was captured means paying somebody money they never gave
            // the platform. It is the one error here that cannot be corrected by another
            // refund, so it is refused rather than clamped.
            return Error.Conflict(
                "payment.over_refund",
                $"Only {RefundablePaise} paise remain refundable on this order.");
        }

        RefundedPaise += amountPaise;

        Status = RefundedPaise >= AmountPaise
            ? OrderStatus.Refunded
            : OrderStatus.PartiallyRefunded;

        ModifiedAtUtc = nowUtc;

        _ledger.Add(LedgerEntry.Refund(
            Guid.CreateVersion7(), SocietyId, Id, amountPaise, gatewayRefundId, reason, nowUtc));

        return Result.Success();
    }
}

public enum LedgerEntryKind
{
    Capture = 0,
    Refund = 1,
    Payout = 2,
}

/// <summary>
/// An append-only record of money moving.
///
/// <para>
/// Never updated and never deleted. The running totals on the order are a convenience; this is
/// the truth, and it is what a reconciliation against the gateway's own statement compares
/// against. A mutable ledger is not a ledger.
/// </para>
///
/// <para>
/// Signed amounts: a capture is positive, a refund and a payout negative. Summing the column
/// gives what the platform holds, which means reconciliation is a <c>SUM</c> rather than a
/// procedure somebody has to get right.
/// </para>
/// </summary>
public sealed class LedgerEntry : Entity, ITenantScoped
{
    private LedgerEntry() { }

    private LedgerEntry(
        Guid id,
        Guid societyId,
        Guid orderId,
        LedgerEntryKind kind,
        long amountPaise,
        string gatewayReference,
        string? reason,
        DateTimeOffset occurredAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        OrderId = orderId;
        Kind = kind;
        AmountPaise = amountPaise;
        GatewayReference = gatewayReference;
        Reason = reason;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public Guid OrderId { get; private set; }

    public LedgerEntryKind Kind { get; private set; }

    /// <summary>Signed. Positive in, negative out.</summary>
    public long AmountPaise { get; private set; }

    public string GatewayReference { get; private set; } = string.Empty;

    public string? Reason { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static LedgerEntry Capture(
        Guid id, Guid societyId, Guid orderId, long amountPaise,
        string reference, DateTimeOffset nowUtc) =>
        new(id, societyId, orderId, LedgerEntryKind.Capture, amountPaise, reference, null, nowUtc);

    public static LedgerEntry Refund(
        Guid id, Guid societyId, Guid orderId, long amountPaise,
        string reference, string reason, DateTimeOffset nowUtc) =>
        new(id, societyId, orderId, LedgerEntryKind.Refund, -amountPaise, reference, reason, nowUtc);

    public static LedgerEntry Payout(
        Guid id, Guid societyId, Guid orderId, long amountPaise,
        string reference, DateTimeOffset nowUtc) =>
        new(id, societyId, orderId, LedgerEntryKind.Payout, -amountPaise, reference, null, nowUtc);
}
