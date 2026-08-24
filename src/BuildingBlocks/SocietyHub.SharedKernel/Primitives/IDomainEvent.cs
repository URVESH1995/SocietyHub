namespace SocietyHub.SharedKernel.Primitives;

/// <summary>
/// Something that happened inside a single aggregate, raised and handled in-process
/// within the same transaction. Not to be confused with an integration event, which
/// crosses a service boundary over RabbitMQ.
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAtUtc { get; }
}

/// <summary>Base record so concrete events only declare their payload.</summary>
public abstract record DomainEvent : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();

    public DateTimeOffset OccurredAtUtc { get; } = DateTimeOffset.UtcNow;
}
