using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocietyHub.Contracts;
using SocietyHub.Persistence.Outbox;

namespace SocietyHub.Messaging;

/// <summary>
/// Collects consumer registrations and the lane each belongs to.
/// </summary>
public sealed class SocietyHubMessagingBuilder
{
    internal List<(Type ConsumerType, MessageLane Lane)> Consumers { get; } = [];

    /// <summary>
    /// Registers a consumer on a lane.
    ///
    /// The lane is chosen per consumer rather than inherited from the message, because the
    /// same event can warrant different urgency in different services: Notification handling
    /// <c>ComplaintRaised</c> needs to be prompt, Reporting handling it does not.
    /// </summary>
    public SocietyHubMessagingBuilder AddConsumer<TConsumer>(MessageLane lane = MessageLane.Normal)
        where TConsumer : class, IConsumer
    {
        Consumers.Add((typeof(TConsumer), lane));
        return this;
    }
}

public static class MessagingRegistration
{
    /// <summary>
    /// Wires MassTransit over RabbitMQ with one queue per lane.
    /// </summary>
    /// <param name="serviceName">Short service name, used in queue names.</param>
    public static IServiceCollection AddSocietyHubMessaging(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<SocietyHubMessagingBuilder>? configure = null)
    {
        var builder = new SocietyHubMessagingBuilder();
        configure?.Invoke(builder);

        services.AddMassTransit(bus =>
        {
            foreach (var (consumerType, _) in builder.Consumers)
            {
                bus.AddConsumer(consumerType);
            }

            bus.UsingRabbitMq((context, cfg) =>
            {
                var connectionString = configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException(
                        "No 'rabbitmq' connection string. Aspire supplies it via WithReference.");

                cfg.Host(new Uri(connectionString));

                // One receive endpoint per lane that actually has consumers. Creating empty
                // queues would leave unmonitored, permanently idle queues in the broker.
                foreach (var lane in builder.Consumers
                             .Select(c => c.Lane)
                             .Distinct()
                             .OrderBy(lane => lane))
                {
                    var laneConsumers = builder.Consumers
                        .Where(c => c.Lane == lane)
                        .Select(c => c.ConsumerType)
                        .ToList();

                    cfg.ReceiveEndpoint(MessageLanes.QueueName(serviceName, lane), endpoint =>
                    {
                        endpoint.PrefetchCount = MessageLanes.PrefetchFor(lane);
                        endpoint.ConcurrentMessageLimit = MessageLanes.ConcurrencyFor(lane);

                        // Retries here are for genuinely transient faults only — a brief
                        // database blip. Anything persistent belongs in the dead-letter queue
                        // where a human can see it, not retried forever in the background.
                        endpoint.UseMessageRetry(retry => retry.Intervals(
                            TimeSpan.FromSeconds(1),
                            TimeSpan.FromSeconds(5),
                            TimeSpan.FromSeconds(15)));

                        foreach (var consumerType in laneConsumers)
                        {
                            endpoint.ConfigureConsumer(context, consumerType);
                        }
                    });
                }

                // Nothing else is auto-configured. Without this MassTransit would create a
                // default endpoint per consumer and quietly undo the lane separation.
            });
        });

        services.AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();

        return services;
    }
}

/// <summary>
/// Publishes outbox messages onto the bus.
///
/// Only <see cref="OutboxProcessor"/> calls this. Handlers stage events in the outbox instead,
/// so nothing is ever published for a transaction that later rolled back.
/// </summary>
public sealed class MassTransitIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitIntegrationEventPublisher(IPublishEndpoint publishEndpoint) =>
        _publishEndpoint = publishEndpoint;

    public Task PublishAsync(
        IntegrationEvent integrationEvent,
        CancellationToken cancellationToken = default) =>
        // Published as its concrete type so MassTransit routes by message type rather than by
        // the IntegrationEvent base, which every consumer would otherwise receive.
        _publishEndpoint.Publish(
            integrationEvent,
            integrationEvent.GetType(),
            context =>
            {
                context.MessageId = integrationEvent.EventId;
                context.CorrelationId = Guid.TryParse(integrationEvent.CorrelationId, out var id)
                    ? id
                    : null;

                // Carried as headers so a message can be triaged in the broker's UI without
                // deserialising the body — which is exactly what you want at 3am.
                context.Headers.Set("society-id", integrationEvent.SocietyId.ToString());
                context.Headers.Set("lane", MessageLanes.For(integrationEvent).ToString());
            },
            cancellationToken);
}
