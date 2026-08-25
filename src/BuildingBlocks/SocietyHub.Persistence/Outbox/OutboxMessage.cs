namespace SocietyHub.Persistence.Outbox;

/// <summary>
/// An integration event durably queued for publication, written in the same transaction as
/// the state change that caused it.
///
/// This exists to close the dual-write gap. A handler that saves a complaint and then calls
/// RabbitMQ performs two writes to two systems with no shared transaction: if the broker call
/// fails, or the process dies between them, the complaint exists and nobody is ever told. If
/// the publish is moved first, the reverse happens and a notification goes out for a
/// complaint that was never saved. Writing the message to the same database in the same
/// transaction makes the two atomic, and a background processor forwards it afterwards.
///
/// The cost of that guarantee is honest and worth stating: delivery becomes at-least-once,
/// never exactly-once. The processor can publish a message and die before marking it done, so
/// every consumer must be idempotent on <see cref="Contracts.IntegrationEvent.EventId"/>.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>
    /// Deliberately the <c>EventId</c> of the integration event rather than a fresh value, so
    /// the row and the message a consumer deduplicates on carry the same identity.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>Stable event type name, resolved back to a CLR type on the way out.</summary>
    public required string EventType { get; init; }

    /// <summary>The event serialised as JSON.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Denormalised from the event for tracing and for triaging a poison message to the
    /// society it belongs to. Not a foreign key: the outbox outlives the row that caused it.
    /// </summary>
    public required Guid SocietyId { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Set once the broker has accepted the message. Null while pending.</summary>
    public DateTimeOffset? ProcessedAtUtc { get; set; }

    /// <summary>Delivery attempts so far, driving the backoff schedule.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// When this message becomes eligible for a delivery attempt. Set to the occurrence time
    /// on insert, so a fresh message is eligible at once and the processor's predicate stays a
    /// single sargable comparison rather than a null check ORed with a range test.
    /// </summary>
    public required DateTimeOffset NextAttemptAtUtc { get; set; }

    /// <summary>Last failure, kept for diagnosis. Truncated to fit the column.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Set when the message has exhausted its attempts. It stops being retried but is never
    /// deleted, because a message nobody can deliver is exactly the one worth keeping: it has
    /// to be visible to an operator rather than disappearing from a queue at 3am.
    /// </summary>
    public bool IsPoisoned { get; set; }

    /// <summary>Correlates the event back to the request that produced it.</summary>
    public string? CorrelationId { get; init; }
}
