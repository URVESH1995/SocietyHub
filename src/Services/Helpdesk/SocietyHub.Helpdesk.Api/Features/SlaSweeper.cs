using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SocietyHub.Contracts.Helpdesk;
using SocietyHub.Helpdesk.Api.Domain;
using SocietyHub.Helpdesk.Api.Persistence;
using SocietyHub.Persistence.Outbox;

namespace SocietyHub.Helpdesk.Api.Features;

public sealed class SlaSweeperOptions
{
    public const string SectionName = "SlaSweeper";

    /// <summary>
    /// How often to look for breaches.
    ///
    /// Five minutes, not one. The deadline granularity residents care about is hours, and a
    /// tighter loop would scan the open-complaint index twelve times more often for an
    /// escalation nobody would notice arriving sooner.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Bounded so one very overdue society cannot starve the rest of a pass.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Minimum gap between escalations of the same ticket.
    ///
    /// Without it, every pass would re-alert the same committee members every five minutes
    /// until somebody acted — which trains them to mute the alerts entirely, and the next
    /// genuine breach goes unread.
    /// </summary>
    public TimeSpan EscalationCooldown { get; set; } = TimeSpan.FromHours(4);

    /// <summary>The rung at which nobody further up exists to notify.</summary>
    public int MaxEscalationLevel { get; set; } = 3;
}

/// <summary>
/// Watches open complaints and escalates the ones that have missed their promise.
///
/// This is what makes the 24-hour SLA a commitment rather than a marketing line. Without it,
/// a breach is only noticed when a resident complains about the complaint.
///
/// The ladder is deliberately gradual — assignee, then society admin, then committee — and
/// rate limited, because an escalation that fires every five minutes is one that gets muted.
/// </summary>
public sealed class SlaSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SlaSweeperOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SlaSweeper> _logger;

    public SlaSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<SlaSweeperOptions> options,
        TimeProvider timeProvider,
        ILogger<SlaSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var escalated = await SweepAsync(stoppingToken);

                if (escalated > 0)
                {
                    _logger.LogWarning("SLA sweep escalated {Count} breached complaints.", escalated);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never let the loop die. A database blip must pause escalation, not stop it
                // silently until someone restarts the service — which is the failure mode
                // where breaches accumulate invisibly.
                _logger.LogError(ex, "SLA sweep failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(_options.Value.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown, not a fault.
            }
        }
    }

    /// <summary>
    /// One pass. Exposed so tests can drive it directly rather than racing the timer.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var now = _timeProvider.GetUtcNow();

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        // IgnoreQueryFilters because this runs outside any request and has no tenant. It
        // legitimately spans every society — which is exactly why it is a background service
        // and not something an endpoint can reach.
        var breached = await context.Complaints
            .IgnoreQueryFilters()
            .Where(c => c.Status != ComplaintStatus.Closed
                        && c.Status != ComplaintStatus.Rejected
                        && c.ResolvedAtUtc == null
                        && c.SlaDueAtUtc < now
                        && c.EscalationLevel < options.MaxEscalationLevel
                        && (c.LastEscalatedAtUtc == null
                            || c.LastEscalatedAtUtc < now - options.EscalationCooldown))
            // Longest overdue first: the ticket that has been waiting two days matters more
            // than the one that tipped over a minute ago.
            .OrderBy(c => c.SlaDueAtUtc)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        if (breached.Count == 0)
        {
            return 0;
        }

        foreach (var complaint in breached)
        {
            var level = complaint.Escalate(now);

            // Staged in the outbox like every other publish, so an escalation is never
            // announced for a transaction that then rolls back.
            outbox.Enqueue(new ComplaintSlaBreached
            {
                SocietyId = complaint.SocietyId,
                ComplaintId = complaint.Id,
                TicketNumber = complaint.TicketNumber,
                FlatId = complaint.FlatId,
                SlaDueAtUtc = complaint.SlaDueAtUtc,
                EscalationLevel = level,
            });

            _logger.LogWarning(
                "Complaint {Ticket} breached its SLA (due {Due:u}); escalated to level {Level}.",
                complaint.TicketNumber,
                complaint.SlaDueAtUtc,
                level);
        }

        await context.SaveChangesAsync(cancellationToken);

        return breached.Count;
    }
}

/// <summary>
/// Who hears about a breach at each rung.
///
/// Lives here rather than on the aggregate because it is a delivery concern — the complaint
/// knows it is late, not who ought to be told. Notification consumes the level and resolves
/// the audience.
/// </summary>
public static class EscalationMatrix
{
    public static string AudienceFor(int escalationLevel) => escalationLevel switch
    {
        1 => "assignee",
        2 => "society-admin",
        3 => "committee",
        _ => "committee",
    };

    /// <summary>
    /// The lane a breach notification rides.
    ///
    /// A repeatedly ignored complaint gets more urgent delivery, not merely a wider audience.
    /// Level three means two people have already failed to act.
    /// </summary>
    public static string LaneFor(int escalationLevel) =>
        escalationLevel >= 3 ? "Gate" : "Normal";
}
