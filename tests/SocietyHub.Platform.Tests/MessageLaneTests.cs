using SocietyHub.Contracts.Gate;
using SocietyHub.Contracts.Helpdesk;
using SocietyHub.Contracts.Vision;
using SocietyHub.Messaging;

namespace SocietyHub.Platform.Tests;

/// <summary>
/// Lane assignment decides whether an SOS alert waits behind a notice broadcast. These assert
/// the classification, not the transport.
/// </summary>
public sealed class MessageLaneTests
{
    [Theory]
    [InlineData(typeof(SosRaised))]
    [InlineData(typeof(FireOrSmokeDetected))]
    [InlineData(typeof(FallDetected))]
    public void Life_safety_events_are_critical(Type eventType) =>
        Assert.Equal(MessageLane.Critical, MessageLanes.For(eventType));

    [Theory]
    [InlineData(typeof(VisitorCheckedIn))]
    [InlineData(typeof(VisitorCheckedOut))]
    [InlineData(typeof(TailgatingDetected))]
    [InlineData(typeof(VehicleDetected))]
    public void Gate_traffic_gets_its_own_lane(Type eventType) =>
        Assert.Equal(MessageLane.Gate, MessageLanes.For(eventType));

    [Fact]
    public void An_unclassified_event_defaults_to_normal_rather_than_bulk()
    {
        // Forgetting to classify a new event should make it ordinary, not quietly
        // deprioritise it into the lane that is allowed to lag.
        Assert.Equal(MessageLane.Normal, MessageLanes.For(typeof(ComplaintRaised)));
    }

    [Fact]
    public void Critical_runs_at_low_concurrency_on_purpose()
    {
        // Concurrency raises throughput and, with it, the chance a message waits behind others
        // inside one consumer. For an SOS alert latency beats throughput, and there are never
        // many at once.
        Assert.True(
            MessageLanes.ConcurrencyFor(MessageLane.Critical)
            < MessageLanes.ConcurrencyFor(MessageLane.Gate));

        Assert.True(
            MessageLanes.PrefetchFor(MessageLane.Critical)
            < MessageLanes.PrefetchFor(MessageLane.Bulk));
    }

    [Fact]
    public void Every_lane_has_a_positive_concurrency_and_prefetch()
    {
        // A lane configured to zero would silently stop consuming — a queue that fills forever
        // while every health check stays green.
        foreach (var lane in Enum.GetValues<MessageLane>())
        {
            Assert.True(MessageLanes.ConcurrencyFor(lane) > 0, $"{lane} concurrency");
            Assert.True(MessageLanes.PrefetchFor(lane) > 0, $"{lane} prefetch");
        }
    }

    [Fact]
    public void Queue_names_are_unique_per_service_and_lane()
    {
        // Notification's gate queue and Reporting's gate queue must drain independently; a
        // shared name would make them compete for the same messages.
        var names = new[] { "notification", "reporting" }
            .SelectMany(service => Enum.GetValues<MessageLane>()
                .Select(lane => MessageLanes.QueueName(service, lane)))
            .ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.StartsWith("societyhub.", n, StringComparison.Ordinal));
    }
}
