using MassTransit;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Drives;
using SocietyHub.Drives.Api.Domain;
using SocietyHub.Drives.Api.Persistence;
using SocietyHub.Messaging;
using SocietyHub.Persistence.Inbox;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Drives.Api.Features;

/// <summary>
/// Marks an enrolment settled once Payments confirms the money went back.
///
/// The last link in the compensation chain. The drive stays in Refunding until every
/// outstanding refund has produced one of these, which is what makes "cancelled" mean nobody
/// is out of pocket rather than merely that the drive stopped.
/// </summary>
public sealed class DriveRefundIssuedConsumer : IdempotentConsumer<DriveRefundIssued>
{
    private readonly DrivesDbContext _drives;
    private readonly IOutbox _outbox;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DriveRefundIssuedConsumer> _logger;

    public DriveRefundIssuedConsumer(
        IInbox inbox,
        DbContext context,
        DrivesDbContext drives,
        IOutbox outbox,
        TimeProvider timeProvider,
        ILogger<DriveRefundIssuedConsumer> logger)
        : base(inbox, context, logger)
    {
        _drives = drives;
        _outbox = outbox;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override string ConsumerName => "drives.refund-issued";

    protected override async Task HandleAsync(
        DriveRefundIssued message,
        ConsumeContext<DriveRefundIssued> context,
        CancellationToken cancellationToken)
    {
        // No request, so no society claim. Stated explicitly, as everywhere else background
        // work writes tenant-scoped rows.
        using var tenantScope = TenantScope.For(message.SocietyId);

        var drive = await _drives.Drives
            .Include(d => d.Enrolments)
            .FirstOrDefaultAsync(d => d.Id == message.DriveId, cancellationToken);

        if (drive is null)
        {
            _logger.LogWarning(
                "Refund confirmed for unknown drive {DriveId}.", message.DriveId);

            return;
        }

        var wasRefunding = drive.Status == DriveStatus.Refunding;

        var result = drive.RecordRefund(
            message.EnrolmentId, message.RefundReference, _timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            _logger.LogWarning(
                "Could not record refund on drive {DriveId}: {Code}",
                message.DriveId, result.Error.Code);

            return;
        }

        // Announced only on the transition, not on every refund. A drive with sixty
        // participants would otherwise publish sixty completion events, fifty-nine of which
        // are wrong.
        if (wasRefunding && drive.Status == DriveStatus.Cancelled)
        {
            _outbox.Enqueue(new DriveRefundsCompleted
            {
                SocietyId = drive.SocietyId,
                DriveId = drive.Id,
                RefundsIssued = drive.Enrolments.Count(e => e.Status == EnrolmentStatus.Refunded),
                OccurredAtUtc = _timeProvider.GetUtcNow(),
            });

            _logger.LogInformation(
                "Drive {DriveId} is fully refunded and now cancelled.", drive.Id);
        }

        await _drives.SaveChangesAsync(cancellationToken);
    }
}
