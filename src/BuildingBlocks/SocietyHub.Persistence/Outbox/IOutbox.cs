using SocietyHub.Contracts;

namespace SocietyHub.Persistence.Outbox;

/// <summary>
/// Queues an integration event for publication as part of the current transaction.
///
/// Handlers call this instead of touching a message broker. Nothing is sent when it is
/// called — the event is staged in the same <c>DbContext</c> as the state change, so it is
/// committed or rolled back together with it, and forwarded afterwards by
/// <see cref="OutboxProcessor"/>.
/// </summary>
public interface IOutbox
{
    /// <summary>
    /// Stages <paramref name="integrationEvent"/> for publication.
    ///
    /// Nothing is written until the caller saves. A handler that enqueues and then throws
    /// publishes nothing, which is the entire point.
    /// </summary>
    void Enqueue(IntegrationEvent integrationEvent);
}

/// <summary>
/// Sends an integration event to the broker. Implemented over MassTransit in P1-03; kept as
/// an interface here so the outbox and its tests do not depend on a running RabbitMQ.
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Publishes to the broker. Must throw on failure — the processor treats a returned
    /// task that completed as proof of acceptance and will mark the message done.
    /// </summary>
    Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
