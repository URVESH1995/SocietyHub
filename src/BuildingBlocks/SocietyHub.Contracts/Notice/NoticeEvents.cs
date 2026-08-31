namespace SocietyHub.Contracts.Notice;

/// <summary>
/// A notice went up on the board.
///
/// Carries the audience rather than a recipient list. Resolving which residents that means is
/// the Notification service's job and depends on data it can reach; putting 600 user ids in an
/// event would make the message enormous and stale the moment a flat changes hands.
/// </summary>
public sealed record NoticePublished : IntegrationEvent
{
    public required Guid NoticeId { get; init; }

    public required string Category { get; init; }

    public required string Title { get; init; }

    /// <summary>
    /// A short excerpt for the push notification. The full body is deliberately not on the
    /// event — notices run to 8,000 characters, and a fan-out to 600 residents should not
    /// carry that through the broker to render one line on a lock screen.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>Everyone, Towers, Flats or Committee.</summary>
    public required string Audience { get; init; }

    /// <summary>Comma-separated tower names when the audience is Towers.</summary>
    public string? TargetTowers { get; init; }

    /// <summary>Comma-separated flat ids when the audience is Flats.</summary>
    public string? TargetFlatIds { get; init; }

    public required bool RequiresAcknowledgement { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

/// <summary>A vote opened. Residents are told once here and once again before it closes.</summary>
public sealed record PollOpened : IntegrationEvent
{
    public required Guid PollId { get; init; }

    public required string Question { get; init; }

    /// <summary>Opinion or Resolution.</summary>
    public required string Kind { get; init; }

    public required DateTimeOffset ClosesAtUtc { get; init; }

    public required int EligibleFlatCount { get; init; }
}

/// <summary>
/// A vote finished, with the outcome and whether quorum was reached.
///
/// Quorum is reported explicitly rather than left to be inferred from the counts, because a
/// failed vote and a lopsided one look identical from the numbers alone.
/// </summary>
public sealed record PollClosed : IntegrationEvent
{
    public required Guid PollId { get; init; }

    public required string Question { get; init; }

    public required int Turnout { get; init; }

    public required int EligibleFlatCount { get; init; }

    public required bool QuorumMet { get; init; }

    /// <summary>Null when no option won outright, or when quorum was not reached.</summary>
    public string? WinningOption { get; init; }
}
