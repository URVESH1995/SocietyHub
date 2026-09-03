using MassTransit;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Drives;
using SocietyHub.Messaging;
using SocietyHub.Payments.Api.Domain;
using SocietyHub.Payments.Api.Gateway;
using SocietyHub.Payments.Api.Persistence;
using SocietyHub.Persistence.Inbox;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Payments.Api.Features;

/// <summary>
/// Issues the refunds the Drives service asks for.
///
/// <para>
/// The far end of the compensation path, and the place where a mistake costs actual money. The
/// Drives worker re-derives outstanding refunds on every pass and will legitimately ask for the
/// same one repeatedly — after a lost response, after a restart, after a webhook that never
/// arrived. Every one of those has to result in exactly one refund.
/// </para>
///
/// <para>
/// Three defences, deliberately overlapping, because the failure is unrecoverable:
/// the inbox deduplicates the message; the ledger refuses a second refund with the same
/// gateway reference; and the gateway is called with an idempotency key derived from the
/// enrolment. Any one would usually be enough. Money is where "usually" stops being a word
/// worth using.
/// </para>
/// </summary>
public sealed class DriveRefundConsumer : IdempotentConsumer<DriveRefundRequested>
{
    private readonly PaymentsDbContext _context;
    private readonly IPaymentGateway _gateway;
    private readonly IPublishEndpoint _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DriveRefundConsumer> _logger;

    public DriveRefundConsumer(
        IInbox inbox,
        DbContext context,
        PaymentsDbContext payments,
        IPaymentGateway gateway,
        IPublishEndpoint publisher,
        TimeProvider timeProvider,
        ILogger<DriveRefundConsumer> logger)
        : base(inbox, context, logger)
    {
        _context = payments;
        _gateway = gateway;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override string ConsumerName => "payments.drive-refund";

    protected override async Task HandleAsync(
        DriveRefundRequested message,
        ConsumeContext<DriveRefundRequested> context,
        CancellationToken cancellationToken)
    {
        // The consumer runs outside any request, so it carries no society claim and the
        // write-side guard would reject every save. Stating the society explicitly is the
        // documented way to do that.
        using var tenantScope = TenantScope.For(message.SocietyId);

        var order = await _context.Orders
            .Include(o => o.Ledger)
            .FirstOrDefaultAsync(
                o => o.ReferenceId == message.EnrolmentId && o.Purpose == "drive_enrolment",
                cancellationToken);

        if (order is null)
        {
            // No order means nothing was ever charged. Refunding would be paying somebody
            // money they never gave us, so this is logged loudly and dropped rather than
            // retried — a retry cannot make an order appear.
            _logger.LogError(
                "Refund requested for enrolment {EnrolmentId} with no payment order.",
                message.EnrolmentId);

            return;
        }

        if (order.GatewayPaymentId is null)
        {
            _logger.LogWarning(
                "Refund requested for order {OrderId} that was never captured.", order.Id);

            return;
        }

        if (order.RefundablePaise <= 0)
        {
            // Already fully refunded, almost certainly by an earlier delivery of this same
            // message. Publishing completion again is safe and keeps the drive moving if the
            // previous acknowledgement was the thing that went missing.
            await PublishRefundedAsync(message, order, cancellationToken);

            return;
        }

        var amount = Math.Min(message.AmountPaise, order.RefundablePaise);

        // The idempotency key is the enrolment, not the message. Two deliveries of the same
        // request carry different message ids and must still produce one refund at the gateway.
        var refund = await _gateway.RefundAsync(
            order.GatewayPaymentId,
            amount,
            message.EnrolmentId.ToString("N"),
            cancellationToken);

        if (refund.IsFailure)
        {
            // Thrown so the message is retried. This is the one place where giving up quietly
            // means a resident is permanently out of pocket, so it must stay on the queue
            // until it succeeds or a human looks at the dead letter.
            throw new InvalidOperationException(
                $"Refund failed for order {order.Id}: {refund.Error.Code}");
        }

        var recorded = order.RecordRefund(
            refund.Value.GatewayRefundId,
            refund.Value.AmountPaise,
            message.Reason,
            _timeProvider.GetUtcNow());

        if (recorded.IsFailure)
        {
            _logger.LogWarning(
                "Refund on order {OrderId} was not recorded: {Code}",
                order.Id, recorded.Error.Code);
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Refunded {Amount} paise on order {OrderId} for {Reason}.",
            refund.Value.AmountPaise, order.Id, message.Reason);

        await PublishRefundedAsync(message, order, cancellationToken);
    }

    /// <summary>
    /// Tells Drives the money is back, so it can mark the enrolment settled and — once none
    /// are outstanding — the drive cancelled.
    /// </summary>
    private Task PublishRefundedAsync(
        DriveRefundRequested message, PaymentOrder order, CancellationToken cancellationToken) =>
        _publisher.Publish(
            new DriveRefundIssued
            {
                SocietyId = message.SocietyId,
                DriveId = message.DriveId,
                EnrolmentId = message.EnrolmentId,
                RefundReference = order.Ledger
                    .Where(e => e.Kind == LedgerEntryKind.Refund)
                    .OrderByDescending(e => e.OccurredAtUtc)
                    .Select(e => e.GatewayReference)
                    .FirstOrDefault() ?? string.Empty,
                AmountPaise = order.RefundedPaise,
                OccurredAtUtc = _timeProvider.GetUtcNow(),
            },
            cancellationToken);
}
