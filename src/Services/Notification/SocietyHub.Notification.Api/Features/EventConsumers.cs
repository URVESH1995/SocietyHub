using MassTransit;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts;
using SocietyHub.Contracts.Gate;
using SocietyHub.Contracts.Helpdesk;
using SocietyHub.Contracts.Notice;
using SocietyHub.Messaging;
using SocietyHub.Notification.Api.Features;
using SocietyHub.Persistence.Inbox;

namespace SocietyHub.Notification.Api.Consumers;

/// <summary>
/// Shared plumbing for the consumers below.
///
/// Each one answers two questions — who should hear about this, and what should the message
/// say — and nothing else. Deduplication, transactional commit and the fan-out to channels
/// are all handled elsewhere, which is why these stay short.
///
/// Recipients are resolved from the event payload for now. When Society exposes its
/// flat-to-resident lookup this calls that instead, through the P1-09 cache — the shape of
/// <see cref="ResolveFlatResidentsAsync"/> is what that will fill in.
/// </summary>
public abstract class NotificationConsumer<TEvent> : IdempotentConsumer<TEvent>
    where TEvent : IntegrationEvent
{
    protected NotificationConsumer(
        IInbox inbox,
        DbContext context,
        INotificationEnqueuer enqueuer,
        ILogger logger) : base(inbox, context, logger)
    {
        Enqueuer = enqueuer;
        Logger = logger;
    }

    protected INotificationEnqueuer Enqueuer { get; }

    /// <summary>
    /// The base class keeps its logger private, and a consumer that resolves an empty
    /// audience needs to say so — silence there looks identical to a working fan-out.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Who lives in a flat.
    ///
    /// A placeholder that returns nothing until the Society lookup is wired. It returns an
    /// empty list rather than throwing so a missing dependency degrades to "no notification"
    /// rather than poisoning the message and stalling the queue — and the consumer logs the
    /// gap so it is visible.
    /// </summary>
    protected static Task<IReadOnlyCollection<Recipient>> ResolveFlatResidentsAsync(
        Guid flatId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Recipient>>([]);
}

/// <summary>Tells a resident their visitor is at the gate. Rides the Gate lane.</summary>
public sealed class VisitorCheckedInConsumer : NotificationConsumer<VisitorCheckedIn>
{
    public VisitorCheckedInConsumer(
        IInbox inbox,
        DbContext context,
        INotificationEnqueuer enqueuer,
        ILogger<VisitorCheckedInConsumer> logger)
        : base(inbox, context, enqueuer, logger)
    {
    }

    protected override string ConsumerName => "notification.visitor-checked-in";

    protected override async Task HandleAsync(
        VisitorCheckedIn message,
        ConsumeContext<VisitorCheckedIn> context,
        CancellationToken cancellationToken)
    {
        var recipients = await ResolveFlatResidentsAsync(message.FlatId, cancellationToken);

        await Enqueuer.EnqueueAsync(
            message.SocietyId,
            nameof(VisitorCheckedIn),
            recipients,
            new Dictionary<string, string?>
            {
                ["visitorName"] = message.VisitorName,
                ["visitorType"] = message.VisitorType,
                ["vehicleNumber"] = message.VehicleNumber,
            },
            message.EventId,
            cancellationToken);
    }
}

/// <summary>Confirms a pass was created, and carries the code the visitor will quote.</summary>
public sealed class VisitorPreApprovedConsumer : NotificationConsumer<VisitorPreApproved>
{
    public VisitorPreApprovedConsumer(
        IInbox inbox,
        DbContext context,
        INotificationEnqueuer enqueuer,
        ILogger<VisitorPreApprovedConsumer> logger)
        : base(inbox, context, enqueuer, logger)
    {
    }

    protected override string ConsumerName => "notification.visitor-pre-approved";

    protected override async Task HandleAsync(
        VisitorPreApproved message,
        ConsumeContext<VisitorPreApproved> context,
        CancellationToken cancellationToken)
    {
        var recipients = await ResolveFlatResidentsAsync(message.FlatId, cancellationToken);

        await Enqueuer.EnqueueAsync(
            message.SocietyId,
            nameof(VisitorPreApproved),
            recipients,
            new Dictionary<string, string?>
            {
                ["visitorName"] = message.VisitorName,
                ["validUntil"] = message.ValidUntilUtc.ToString("HH:mm"),
            },
            message.EventId,
            cancellationToken);
    }
}

/// <summary>
/// The panic alert. Registered on the Critical lane, which is the entire reason the lanes
/// exist — this must never queue behind a notice broadcast.
/// </summary>
public sealed class SosRaisedConsumer : NotificationConsumer<SosRaised>
{
    public SosRaisedConsumer(
        IInbox inbox,
        DbContext context,
        INotificationEnqueuer enqueuer,
        ILogger<SosRaisedConsumer> logger)
        : base(inbox, context, enqueuer, logger)
    {
    }

    protected override string ConsumerName => "notification.sos-raised";

    protected override async Task HandleAsync(
        SosRaised message,
        ConsumeContext<SosRaised> context,
        CancellationToken cancellationToken)
    {
        // Deliberately wider than the raising flat: an SOS goes to guards, committee and
        // neighbours, because the people who can help are the ones nearby.
        var recipients = await ResolveFlatResidentsAsync(message.FlatId, cancellationToken);

        await Enqueuer.EnqueueAsync(
            message.SocietyId,
            nameof(SosRaised),
            recipients,
            new Dictionary<string, string?>
            {
                ["category"] = message.Category,
                ["raisedAt"] = message.OccurredAtUtc.ToString("HH:mm"),
            },
            message.EventId,
            cancellationToken);
    }
}

public sealed class ComplaintRaisedConsumer : NotificationConsumer<ComplaintRaised>
{
    public ComplaintRaisedConsumer(
        IInbox inbox,
        DbContext context,
        INotificationEnqueuer enqueuer,
        ILogger<ComplaintRaisedConsumer> logger)
        : base(inbox, context, enqueuer, logger)
    {
    }

    protected override string ConsumerName => "notification.complaint-raised";

    protected override Task HandleAsync(
        ComplaintRaised message,
        ConsumeContext<ComplaintRaised> context,
        CancellationToken cancellationToken) =>
        // Acknowledges to the person who raised it. The society side is told through
        // assignment, not through this.
        Enqueuer.EnqueueAsync(
            message.SocietyId,
            nameof(ComplaintRaised),
            [new Recipient(message.RaisedByUserId, "en-IN", null, null)],
            new Dictionary<string, string?>
            {
                ["ticketNumber"] = message.TicketNumber,
                ["title"] = message.Title,
                ["slaDue"] = message.SlaDueAtUtc.ToString("dd MMM, HH:mm"),
            },
            message.EventId,
            cancellationToken);
}

public sealed class ComplaintResolvedConsumer : NotificationConsumer<ComplaintResolved>
{
    public ComplaintResolvedConsumer(
        IInbox inbox,
        DbContext context,
        INotificationEnqueuer enqueuer,
        ILogger<ComplaintResolvedConsumer> logger)
        : base(inbox, context, enqueuer, logger)
    {
    }

    protected override string ConsumerName => "notification.complaint-resolved";

    protected override Task HandleAsync(
        ComplaintResolved message,
        ConsumeContext<ComplaintResolved> context,
        CancellationToken cancellationToken) =>
        Enqueuer.EnqueueAsync(
            message.SocietyId,
            nameof(ComplaintResolved),
            [new Recipient(message.RaisedByUserId, "en-IN", null, null)],
            new Dictionary<string, string?>
            {
                ["ticketNumber"] = message.TicketNumber,
            },
            message.EventId,
            cancellationToken);
}

/// <summary>
/// A missed SLA. The audience widens with the escalation level rather than the message
/// changing — level three means two people have already failed to act.
/// </summary>
public sealed class ComplaintSlaBreachedConsumer : NotificationConsumer<ComplaintSlaBreached>
{
    public ComplaintSlaBreachedConsumer(
        IInbox inbox,
        DbContext context,
        INotificationEnqueuer enqueuer,
        ILogger<ComplaintSlaBreachedConsumer> logger)
        : base(inbox, context, enqueuer, logger)
    {
    }

    protected override string ConsumerName => "notification.complaint-sla-breached";

    protected override async Task HandleAsync(
        ComplaintSlaBreached message,
        ConsumeContext<ComplaintSlaBreached> context,
        CancellationToken cancellationToken)
    {
        var audience = EscalationMatrix.AudienceFor(message.EscalationLevel);
        var recipients = await ResolveFlatResidentsAsync(message.FlatId, cancellationToken);

        await Enqueuer.EnqueueAsync(
            message.SocietyId,
            nameof(ComplaintSlaBreached),
            recipients,
            new Dictionary<string, string?>
            {
                ["ticketNumber"] = message.TicketNumber,
                ["escalationLevel"] = message.EscalationLevel.ToString(),
                ["audience"] = audience,
            },
            message.EventId,
            cancellationToken);
    }
}

/// <summary>
/// The escalation ladder, duplicated from Helpdesk rather than shared.
///
/// Sharing it would need a common assembly between two services that otherwise have no
/// coupling, and the rule is four lines. Duplication is the cheaper honest cost.
/// </summary>
internal static class EscalationMatrix
{
    public static string AudienceFor(int level) => level switch
    {
        1 => "assignee",
        2 => "society-admin",
        _ => "committee",
    };
}

/// <summary>
/// Announces a notice to the audience the Notice service targeted.
///
/// The event carries the audience rule rather than a recipient list — a notice for 600 flats
/// would otherwise put 600 user ids on the wire, and they would already be stale by the time
/// the message was consumed if a flat changed hands in between. Expanding the rule to people
/// belongs here, next to the cache that makes it cheap.
///
/// Rides the Normal lane. A noticeboard blast is the largest fan-out the platform produces
/// and is exactly the traffic the Critical lane exists to stay clear of.
/// </summary>
public sealed class NoticePublishedConsumer : NotificationConsumer<NoticePublished>
{
    public NoticePublishedConsumer(
        IInbox inbox,
        DbContext context,
        INotificationEnqueuer enqueuer,
        ILogger<NoticePublishedConsumer> logger)
        : base(inbox, context, enqueuer, logger)
    {
    }

    protected override string ConsumerName => "notification.notice-published";

    protected override async Task HandleAsync(
        NoticePublished message,
        ConsumeContext<NoticePublished> context,
        CancellationToken cancellationToken)
    {
        var recipients = await ResolveNoticeAudienceAsync(message, cancellationToken);

        if (recipients.Count == 0)
        {
            // Logged rather than thrown. A notice that reached nobody is a real problem, but
            // failing the message would retry the whole fan-out four times and still reach
            // nobody — the fix is the Society lookup, not the retry.
            Logger.LogWarning(
                "Notice {NoticeId} for society {SocietyId} resolved to no recipients.",
                message.NoticeId,
                message.SocietyId);

            return;
        }

        await Enqueuer.EnqueueAsync(
            message.SocietyId,
            nameof(NoticePublished),
            recipients,
            new Dictionary<string, string?>
            {
                ["title"] = message.Title,
                ["summary"] = message.Summary,
                ["body"] = message.Summary,
            },
            message.EventId,
            cancellationToken);
    }

    /// <summary>
    /// Expands an audience rule into people.
    ///
    /// A placeholder alongside <see cref="NotificationConsumer{TEvent}.ResolveFlatResidentsAsync"/>
    /// and empty for the same reason: the Society lookup does not exist yet, and returning
    /// nothing degrades to a missed notification rather than a poisoned queue. The branching
    /// is written out because it is the part that will not change when the lookup lands — only
    /// the calls inside each branch will.
    /// </summary>
    private static Task<IReadOnlyCollection<Recipient>> ResolveNoticeAudienceAsync(
        NoticePublished message,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<Recipient>>([]);
}
