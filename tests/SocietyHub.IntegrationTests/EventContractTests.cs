using System.Reflection;
using System.Text.Json;
using SocietyHub.Contracts;

namespace SocietyHub.IntegrationTests;

/// <summary>
/// Contract tests for integration events.
///
/// These fail at build time for a class of bug that otherwise fails at 3am in production: a
/// publisher and a consumer live in different services, deploy independently, and nothing in
/// the compiler connects them. Renaming a property is a compile error inside one service and a
/// silent null inside the other.
///
/// A message is also not necessarily consumed the same day it is published — the outbox
/// retries for hours, and a message parked in an error queue may be replayed next week against
/// code that has moved on. So the contract has to hold across versions, not just across
/// services.
/// </summary>
public sealed class EventContractTests
{
    private static readonly Assembly ContractsAssembly = typeof(IntegrationEvent).Assembly;

    private static readonly IReadOnlyList<Type> EventTypes =
    [
        .. ContractsAssembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                        && t.IsAssignableTo(typeof(IntegrationEvent)))
            .OrderBy(t => t.FullName),
    ];

    [Fact]
    public void There_are_events_to_test()
    {
        // Guards every other test in this file. Reflection that silently matches nothing turns
        // a whole suite into an expensive no-op that reports green.
        Assert.NotEmpty(EventTypes);
    }

    [Fact]
    public void Every_event_carries_the_society_it_belongs_to()
    {
        // Without this the consumer cannot set a tenant scope, and a notification for one
        // society reaches another. It is the single most important field on the wire.
        foreach (var type in EventTypes)
        {
            var property = type.GetProperty(nameof(IntegrationEvent.SocietyId));

            Assert.True(
                property is not null && property.PropertyType == typeof(Guid),
                $"{type.Name} must carry a Guid SocietyId.");
        }
    }

    [Fact]
    public void Every_event_carries_a_stable_identity_for_deduplication()
    {
        // The inbox deduplicates on EventId. At-least-once delivery means a duplicate is
        // normal traffic, not an anomaly — without this, a redelivered SosRaised sends a
        // second emergency SMS to 600 people.
        foreach (var type in EventTypes)
        {
            var property = type.GetProperty(nameof(IntegrationEvent.EventId));

            Assert.True(
                property is not null && property.PropertyType == typeof(Guid),
                $"{type.Name} must carry a Guid EventId.");
        }
    }

    [Fact]
    public void Every_event_says_when_it_happened()
    {
        // Consumers order by this and compute SLAs from it. A message that arrives an hour
        // late must still be interpretable against the moment it describes, not the moment it
        // was read.
        foreach (var type in EventTypes)
        {
            var property = type.GetProperty(nameof(IntegrationEvent.OccurredAtUtc));

            Assert.True(
                property is not null && property.PropertyType == typeof(DateTimeOffset),
                $"{type.Name} must carry a DateTimeOffset OccurredAtUtc.");
        }
    }

    [Fact]
    public void No_event_carries_a_naked_DateTime()
    {
        // A DateTime on the wire loses its offset, and the receiving service has no way to
        // recover it. Every time computation in this platform is in a society's local zone,
        // so an ambiguous timestamp is a wrong SLA or a notification at 3am.
        foreach (var type in EventTypes)
        {
            var offenders = type.GetProperties()
                .Where(p => p.PropertyType == typeof(DateTime)
                            || p.PropertyType == typeof(DateTime?))
                .Select(p => p.Name)
                .ToList();

            Assert.True(
                offenders.Count == 0,
                $"{type.Name} uses DateTime for {string.Join(", ", offenders)}. "
                + "Use DateTimeOffset so the offset survives the wire.");
        }
    }

    [Fact]
    public void Every_event_is_a_record_so_it_cannot_be_mutated_in_flight()
    {
        // A consumer that can modify the message it was handed will eventually do so, and the
        // retry after a failure then processes a different message than the first attempt did.
        foreach (var type in EventTypes)
        {
            var isRecord = type.GetMethod("<Clone>$", BindingFlags.Instance
                                                      | BindingFlags.Public
                                                      | BindingFlags.NonPublic) is not null;

            Assert.True(isRecord, $"{type.Name} must be a record.");
        }
    }

    [Fact]
    public void Every_event_round_trips_through_json_unchanged()
    {
        // The actual wire format. A property the serialiser cannot reconstruct — no setter, no
        // matching constructor parameter — deserialises to its default and the consumer sees a
        // plausible-looking message with an empty field.
        foreach (var type in EventTypes)
        {
            var instance = Populate(type);

            var json = JsonSerializer.Serialize(instance, type);
            var revived = JsonSerializer.Deserialize(json, type);

            Assert.NotNull(revived);

            foreach (var property in type.GetProperties().Where(p => p.CanRead))
            {
                var original = property.GetValue(instance);
                var restored = property.GetValue(revived);

                Assert.True(
                    Equals(original, restored),
                    $"{type.Name}.{property.Name} did not survive serialisation: "
                    + $"{original} became {restored}.");
            }
        }
    }

    [Fact]
    public void Every_event_type_name_is_unique_across_services()
    {
        // MassTransit routes on the type name. Two events called ResidentRegistered in
        // different namespaces would land in each other's queues, which is the kind of failure
        // that looks like data corruption rather than a routing mistake.
        var duplicates = EventTypes
            .GroupBy(t => t.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            $"Duplicate event names: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// Builds an instance with every property set to something non-default, so a value silently
    /// lost in serialisation shows up as a difference rather than as default equalling default.
    /// </summary>
    private static object Populate(Type type)
    {
        var instance = Activator.CreateInstance(type, nonPublic: true)
                       ?? throw new InvalidOperationException(
                           $"{type.Name} has no parameterless constructor. Integration events "
                           + "must be constructible by the serialiser.");

        foreach (var property in type.GetProperties().Where(p => p.CanWrite))
        {
            var value = SampleFor(property.PropertyType);

            if (value is not null)
            {
                property.SetValue(instance, value);
            }
        }

        return instance;
    }

    private static object? SampleFor(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(Guid)) { return Guid.CreateVersion7(); }
        if (underlying == typeof(string)) { return "sample"; }
        if (underlying == typeof(int)) { return 42; }
        if (underlying == typeof(long)) { return 42L; }
        if (underlying == typeof(bool)) { return true; }
        if (underlying == typeof(decimal)) { return 42.5m; }
        if (underlying == typeof(double)) { return 42.5d; }

        if (underlying == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        }

        return underlying.IsEnum ? Enum.GetValues(underlying).GetValue(0) : null;
    }
}
