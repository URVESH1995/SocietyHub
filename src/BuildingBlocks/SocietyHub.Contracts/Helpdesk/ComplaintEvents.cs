namespace SocietyHub.Contracts.Helpdesk;

/// <summary>A resident logged a complaint. Starts the 24-hour resolution clock.</summary>
public sealed record ComplaintRaised : IntegrationEvent
{
    public required Guid ComplaintId { get; init; }

    public required string TicketNumber { get; init; }

    public required Guid FlatId { get; init; }

    public required Guid RaisedByUserId { get; init; }

    public required string Category { get; init; }

    public required string Title { get; init; }

    /// <summary>Low, Normal, High or Emergency.</summary>
    public required string Priority { get; init; }

    /// <summary>When the 24-hour SLA expires, computed at creation from the priority.</summary>
    public required DateTimeOffset SlaDueAtUtc { get; init; }
}

public sealed record ComplaintAssigned : IntegrationEvent
{
    public required Guid ComplaintId { get; init; }

    public required string TicketNumber { get; init; }

    public required Guid AssigneeId { get; init; }

    public required string AssigneeName { get; init; }
}

public sealed record ComplaintResolved : IntegrationEvent
{
    public required Guid ComplaintId { get; init; }

    public required string TicketNumber { get; init; }

    public required Guid RaisedByUserId { get; init; }

    public required DateTimeOffset ResolvedAtUtc { get; init; }

    public required bool WithinSla { get; init; }
}

/// <summary>
/// The 24-hour promise was missed. Raised by the SLA sweeper, and the trigger for
/// escalation to the committee.
/// </summary>
public sealed record ComplaintSlaBreached : IntegrationEvent
{
    public required Guid ComplaintId { get; init; }

    public required string TicketNumber { get; init; }

    public required Guid FlatId { get; init; }

    public required DateTimeOffset SlaDueAtUtc { get; init; }

    public required int EscalationLevel { get; init; }
}
