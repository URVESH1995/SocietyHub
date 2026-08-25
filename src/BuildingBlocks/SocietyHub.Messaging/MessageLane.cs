using SocietyHub.Contracts;
using SocietyHub.Contracts.Gate;
using SocietyHub.Contracts.Vision;

namespace SocietyHub.Messaging;

/// <summary>
/// Which queue an event is consumed from.
///
/// The lanes exist because a shared queue makes latency a lottery. A committee broadcasting a
/// notice to a 250-flat society enqueues 600 notifications in one burst; if an SOS alert lands
/// behind them it waits for all 600 to drain. Separate queues with separate consumers mean the
/// bulk backlog is simply irrelevant to the SOS consumer — it is not waiting behind anything,
/// because it is not in that queue.
///
/// This is a consumer-side split. The publisher marks the lane; the queues and their
/// concurrency are what actually deliver the isolation.
/// </summary>
public enum MessageLane
{
    /// <summary>
    /// Life safety: SOS, fire, falls. Target end-to-end under five seconds. Small prefetch so
    /// no consumer is holding a queue of these while working through them.
    /// </summary>
    Critical = 0,

    /// <summary>
    /// Gate traffic. Bursty at 8am and 7pm, and a resident waiting on an arrival notification
    /// is standing at their door.
    /// </summary>
    Gate = 1,

    /// <summary>Complaints, notices, directory changes. The default.</summary>
    Normal = 2,

    /// <summary>
    /// Bulk drives, reporting, marketing fan-out. Deliberately last, and allowed to build a
    /// backlog: nothing here is worse for being a minute late.
    /// </summary>
    Bulk = 3,
}

/// <summary>
/// Maps an event type to its lane.
///
/// A lookup rather than an attribute on each contract, deliberately: <c>Contracts</c> is the
/// public wire schema shared with consumers, and routing is our deployment concern. Putting a
/// <c>[Lane]</c> attribute there would leak our queue topology into everyone's contract.
/// </summary>
public static class MessageLanes
{
    private static readonly Dictionary<Type, MessageLane> Assignments = new()
    {
        // Life safety.
        [typeof(SosRaised)] = MessageLane.Critical,
        [typeof(FireOrSmokeDetected)] = MessageLane.Critical,
        [typeof(FallDetected)] = MessageLane.Critical,

        // Gate.
        [typeof(VisitorPreApproved)] = MessageLane.Gate,
        [typeof(VisitorCheckedIn)] = MessageLane.Gate,
        [typeof(VisitorCheckedOut)] = MessageLane.Gate,
        [typeof(TailgatingDetected)] = MessageLane.Gate,
        [typeof(VehicleDetected)] = MessageLane.Gate,
        [typeof(UnknownVehicleEntered)] = MessageLane.Gate,
        [typeof(PerimeterIntrusionDetected)] = MessageLane.Gate,
    };

    /// <summary>
    /// Unmapped events fall to <see cref="MessageLane.Normal"/>.
    ///
    /// Defaulting to Normal rather than Bulk is deliberate: forgetting to classify a new event
    /// should make it ordinary, not quietly deprioritise it into a queue that is allowed to lag.
    /// </summary>
    public static MessageLane For(Type eventType) =>
        Assignments.TryGetValue(eventType, out var lane) ? lane : MessageLane.Normal;

    public static MessageLane For(IntegrationEvent integrationEvent) =>
        For(integrationEvent.GetType());

    /// <summary>
    /// Queue name for a lane within one service. Named per service, because Notification's
    /// gate queue and Reporting's gate queue must drain independently.
    /// </summary>
    public static string QueueName(string serviceName, MessageLane lane) =>
        $"societyhub.{serviceName.ToLowerInvariant()}.{lane.ToString().ToLowerInvariant()}";

    /// <summary>
    /// How many messages a lane's consumers process at once.
    ///
    /// Critical is intentionally low. Concurrency raises throughput and, with it, the chance
    /// that a message waits behind others in the same consumer — and for an SOS alert, latency
    /// matters far more than throughput. There are never many of them at once.
    /// </summary>
    public static int ConcurrencyFor(MessageLane lane) => lane switch
    {
        MessageLane.Critical => 2,
        MessageLane.Gate => 16,
        MessageLane.Normal => 8,
        MessageLane.Bulk => 4,
        _ => 8,
    };

    public static int PrefetchFor(MessageLane lane) => lane switch
    {
        MessageLane.Critical => 4,
        MessageLane.Gate => 32,
        MessageLane.Normal => 16,
        MessageLane.Bulk => 16,
        _ => 16,
    };
}
