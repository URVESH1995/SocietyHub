using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SocietyHub.Contracts;

namespace SocietyHub.Persistence.Outbox;

/// <summary>
/// Converts integration events to and from the JSON stored in the outbox.
///
/// Type identity is the full CLR type name without assembly or version. Assembly-qualified
/// names would be more precise and are the wrong choice here: a pending outbox row written by
/// the previous deployment must still deserialise after a version bump, and it would not if
/// the stored name pinned an assembly version.
/// </summary>
public static class IntegrationEventSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Unknown members are ignored by default, which is what lets a consumer running last
        // week's build tolerate a field a newer publisher added. That tolerance is the reason
        // integration events may only ever gain optional members, never lose or repurpose one.
    };

    /// <summary>
    /// Every concrete integration event in the Contracts assembly, indexed by type name.
    /// Built once: resolving by reflection per message would be needless work on a hot path.
    /// </summary>
    private static readonly FrozenDictionary<string, Type> KnownEventTypes =
        typeof(IntegrationEvent).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                        && typeof(IntegrationEvent).IsAssignableFrom(t))
            .ToFrozenDictionary(t => t.FullName!, t => t, StringComparer.Ordinal);

    public static string ResolveTypeName(IntegrationEvent integrationEvent) =>
        integrationEvent.GetType().FullName
        ?? throw new InvalidOperationException("Integration events must be named types.");

    public static string Serialize(IntegrationEvent integrationEvent) =>
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), Options);

    /// <summary>
    /// Rebuilds the event. Throws <see cref="UnknownIntegrationEventException"/> when the type
    /// no longer exists, which the processor treats as poison rather than a transient fault —
    /// retrying a deleted type would loop until the end of time.
    /// </summary>
    public static IntegrationEvent Deserialize(string typeName, string payload)
    {
        if (!KnownEventTypes.TryGetValue(typeName, out var eventType))
        {
            throw new UnknownIntegrationEventException(typeName);
        }

        return JsonSerializer.Deserialize(payload, eventType, Options) as IntegrationEvent
               ?? throw new UnknownIntegrationEventException(typeName);
    }

    /// <summary>Exposed so a startup check can assert the registry is populated.</summary>
    public static IReadOnlyCollection<string> RegisteredTypeNames => KnownEventTypes.Keys;
}

/// <summary>
/// The stored event type is not present in this build — renamed, moved or deleted while rows
/// referencing it were still pending.
/// </summary>
public sealed class UnknownIntegrationEventException : Exception
{
    public UnknownIntegrationEventException(string typeName)
        : base($"No integration event type named '{typeName}' exists in this build. " +
               "The type was renamed or removed while outbox rows still referenced it.") =>
        TypeName = typeName;

    public string TypeName { get; }
}
