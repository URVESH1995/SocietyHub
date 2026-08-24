namespace SocietyHub.Contracts;

/// <summary>
/// A fact published onto the bus for other services to react to. Unlike a domain event
/// this crosses a process boundary, so it is a versioned public contract: add optional
/// members freely, never remove or repurpose one.
/// </summary>
public abstract record IntegrationEvent
{
    /// <summary>Deduplication key. Consumers must be idempotent on this value.</summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The society the fact belongs to. Present on every event by design.</summary>
    public required Guid SocietyId { get; init; }

    /// <summary>Ties the event back to the request that caused it, for tracing.</summary>
    public string? CorrelationId { get; init; }
}
