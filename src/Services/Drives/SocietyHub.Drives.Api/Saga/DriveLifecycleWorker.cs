using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Drives;
using SocietyHub.Drives.Api.Domain;
using SocietyHub.Drives.Api.Features;
using SocietyHub.Drives.Api.Persistence;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Drives.Api.Saga;

/// <summary>
/// Drives the lifecycle forward: closes drives at cut-off, and works through refunds until
/// none are outstanding.
///
/// <para>
/// <b>Why a worker rather than a MassTransit saga state machine.</b> The roadmap called for a
/// state machine, and the honest answer after building the aggregate is that the state already
/// lives on <see cref="ServiceDrive"/> — quorum, cut-off and per-enrolment refund progress are
/// all persisted there, transactionally, next to the money they describe. A saga would hold a
/// second copy of that state in its own table and the two would drift, which is a worse
/// problem than the one it solves. What a saga genuinely provides is scheduled messages and
/// correlation; the scheduling is a cut-off timestamp this worker polls, and the correlation is
/// the drive id.
/// </para>
///
/// <para>
/// The compensation path is not a separate mode. Refunds are re-derived from the aggregate on
/// every pass, so a crash mid-way through sixty of them resumes at the sixty-first rather than
/// restarting or stranding the remainder. That property is the entire reason this is a loop
/// over persisted state rather than an in-memory sequence.
/// </para>
/// </summary>
public sealed class DriveLifecycleWorker : BackgroundService
{
    /// <summary>
    /// How often to look.
    ///
    /// A minute is far finer than the domain needs — nobody cares whether a drive closes at
    /// 18:00:00 or 18:00:45 — but it bounds how long a refund sits unretried after a transient
    /// gateway failure, which people do care about.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DriveLifecycleWorker> _logger;

    public DriveLifecycleWorker(
        IServiceScopeFactory scopes,
        TimeProvider timeProvider,
        ILogger<DriveLifecycleWorker> logger)
    {
        _scopes = scopes;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();

                await CloseDuePastCutOffAsync(scope.ServiceProvider, stoppingToken);
                await RequestOutstandingRefundsAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never allowed to kill the loop. A worker that dies on one malformed drive
                // stops closing every other drive on the platform, and the symptom — drives
                // that stay open past their cut-off — looks nothing like the cause.
                _logger.LogError(ex, "Drive lifecycle pass failed. Retrying next interval.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>
    /// Closes every drive whose cut-off has passed, one way or the other.
    /// </summary>
    private async Task CloseDuePastCutOffAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<DrivesDbContext>();
        var rateCards = services.GetRequiredService<IRateCardReader>();
        var outbox = services.GetRequiredService<IOutbox>();

        var now = _timeProvider.GetUtcNow();

        // IgnoreQueryFilters because this runs with no request and therefore no society. The
        // worker legitimately spans every tenant; the writes below re-enter a scope per drive
        // so the guard still sees a society on the way out.
        var due = await context.Drives
            .IgnoreQueryFilters()
            .Include(d => d.Enrolments)
            .Where(d => d.Status == DriveStatus.Open && d.CutOffAtUtc <= now)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var drive in due)
        {
            // Each drive in its own tenant scope. Without it the write-side guard rejects
            // every save, because a background pass has no society claim — the same failure
            // that stopped Identity and Society booting.
            using var tenantScope = TenantScope.For(drive.SocietyId);

            if (drive.HasReachedQuorum)
            {
                await ConfirmAsync(drive, rateCards, outbox, now, cancellationToken);
            }
            else
            {
                await CancelAsync(drive, outbox, now, cancellationToken);
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ConfirmAsync(
        ServiceDrive drive,
        IRateCardReader rateCards,
        IOutbox outbox,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var price = await rateCards.UnitPriceForAsync(
            drive.RateCardId, drive.ActiveUnitCount, cancellationToken);

        if (price.IsFailure)
        {
            // The vendor service is unreachable, or the card has gone. Leaving the drive open
            // and retrying next pass is right: closing it at a guessed price would charge
            // people a number nobody agreed to, and cancelling a drive that met its quorum
            // over a transient outage would be worse still.
            _logger.LogError(
                "Cannot price drive {DriveId} at cut-off: {Code}. Leaving it open to retry.",
                drive.Id, price.Error.Code);

            return;
        }

        var result = drive.CloseWithQuorum(price.Value, now);

        if (result.IsFailure)
        {
            _logger.LogError(
                "Closing drive {DriveId} failed: {Code}", drive.Id, result.Error.Code);

            return;
        }

        outbox.Enqueue(new DriveConfirmed
        {
            SocietyId = drive.SocietyId,
            DriveId = drive.Id,
            ServiceCode = drive.ServiceCode,
            VendorId = drive.VendorId,
            Participants = drive.ActiveParticipantCount,
            TotalUnits = drive.ActiveUnitCount,
            FinalUnitPricePaise = price.Value,
            ServiceDateUtc = drive.ServiceDateUtc!.Value,
            OccurredAtUtc = now,
        });

        // Early joiners paid a higher slab price and are owed the difference. Requested here,
        // at close, rather than left for anyone to reconcile later — a partial refund nobody
        // triggers is money the platform has quietly kept.
        foreach (var enrolment in drive.Enrolments.Where(e => e.RefundDuePaise > 0))
        {
            outbox.Enqueue(new DriveRefundRequested
            {
                SocietyId = drive.SocietyId,
                DriveId = drive.Id,
                EnrolmentId = enrolment.Id,
                UserId = enrolment.UserId,
                PaymentReference = enrolment.PaymentReference ?? string.Empty,
                AmountPaise = enrolment.RefundDuePaise,
                Reason = "price_settled",
                OccurredAtUtc = now,
            });
        }

        _logger.LogInformation(
            "Drive {DriveId} confirmed with {Participants} participants at {Price} paise/unit.",
            drive.Id, drive.ActiveParticipantCount, price.Value);
    }

    private async Task CancelAsync(
        ServiceDrive drive, IOutbox outbox, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var participants = drive.ActiveParticipantCount;

        var result = drive.CloseWithoutQuorum(
            $"Reached {participants} of {drive.Quorum} participants by the cut-off.", now);

        if (result.IsFailure)
        {
            return;
        }

        outbox.Enqueue(new DriveCancelled
        {
            SocietyId = drive.SocietyId,
            DriveId = drive.Id,
            ServiceCode = drive.ServiceCode,
            Participants = participants,
            Quorum = drive.Quorum,
            Reason = drive.CancellationReason ?? "Quorum not reached.",
            RefundsDue = drive.OutstandingRefunds.Count,
            OccurredAtUtc = now,
        });

        _logger.LogInformation(
            "Drive {DriveId} cancelled at {Participants}/{Quorum}. {Refunds} refunds due.",
            drive.Id, participants, drive.Quorum, drive.OutstandingRefunds.Count);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Asks for every refund that is still outstanding.
    ///
    /// Re-derived from the aggregate on every pass rather than driven from a list built once.
    /// That is what makes the compensation resumable: a process killed after forty of sixty
    /// refunds comes back, reads the twenty still marked RefundDue, and asks for those.
    ///
    /// Requesting the same refund twice is safe by construction — the payment service
    /// deduplicates on the enrolment id — which is the property that lets this be a simple
    /// retry rather than a distributed transaction.
    /// </summary>
    private async Task RequestOutstandingRefundsAsync(
        IServiceProvider services, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<DrivesDbContext>();
        var outbox = services.GetRequiredService<IOutbox>();

        var now = _timeProvider.GetUtcNow();

        var refunding = await context.Drives
            .IgnoreQueryFilters()
            .Include(d => d.Enrolments)
            .Where(d => d.Status == DriveStatus.Refunding)
            .Take(20)
            .ToListAsync(cancellationToken);

        foreach (var drive in refunding)
        {
            using var tenantScope = TenantScope.For(drive.SocietyId);

            foreach (var enrolment in drive.OutstandingRefunds)
            {
                outbox.Enqueue(new DriveRefundRequested
                {
                    SocietyId = drive.SocietyId,
                    DriveId = drive.Id,
                    EnrolmentId = enrolment.Id,
                    UserId = enrolment.UserId,
                    PaymentReference = enrolment.PaymentReference ?? string.Empty,
                    AmountPaise = enrolment.AmountChargedPaise,
                    Reason = "quorum_missed",
                    OccurredAtUtc = now,
                });
            }

            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
