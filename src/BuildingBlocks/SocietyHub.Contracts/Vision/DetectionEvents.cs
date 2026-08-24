namespace SocietyHub.Contracts.Vision;

// ---------------------------------------------------------------------------
// Vehicle
// ---------------------------------------------------------------------------

/// <summary>
/// A number plate was read at a gate. The workhorse of the whole feature: it needs no
/// consent negotiation, works at night, and answers the question societies actually ask —
/// whose car is in the visitor bay.
/// </summary>
public sealed record VehicleDetected : VisionEvent
{
    /// <summary>Normalised registration, e.g. <c>MH12AB1234</c>. Spacing varies by state.</summary>
    public required string PlateNumber { get; init; }

    /// <summary>Inbound or Outbound.</summary>
    public required string Direction { get; init; }

    /// <summary>Set when the plate matches a vehicle registered to a flat.</summary>
    public Guid? MatchedVehicleId { get; init; }

    public Guid? MatchedFlatId { get; init; }

    /// <summary>Car, Bike, Auto, Truck or Unknown.</summary>
    public required string VehicleType { get; init; }
}

/// <summary>
/// A vehicle entered that matches no resident and no expected visitor. Not an accusation —
/// most are legitimate deliveries — but the guard should have logged it and did not.
/// </summary>
public sealed record UnknownVehicleEntered : VisionEvent
{
    public required string PlateNumber { get; init; }

    public required string VehicleType { get; init; }

    /// <summary>True when the plate has entered repeatedly without ever being logged.</summary>
    public required bool IsRepeatOccurrence { get; init; }
}

// ---------------------------------------------------------------------------
// Perimeter and access
// ---------------------------------------------------------------------------

/// <summary>Someone crossed into a restricted zone — perimeter wall, terrace, pump room.</summary>
public sealed record PerimeterIntrusionDetected : VisionEvent
{
    public required int PersonCount { get; init; }

    /// <summary>True outside the society's configured active hours.</summary>
    public required bool IsOutsideActiveHours { get; init; }
}

/// <summary>
/// A person remained in one place well past what the zone expects. Tuned per zone, because
/// loitering at a lobby sofa is normal and loitering at a compound wall at 3am is not.
/// </summary>
public sealed record LoiteringDetected : VisionEvent
{
    public required int DwellSeconds { get; init; }

    public required int PersonCount { get; init; }
}

/// <summary>
/// More people passed the gate than the pass admitted. The single most useful access-control
/// signal there is, and it needs no identification of anybody — only counting.
/// </summary>
public sealed record TailgatingDetected : VisionEvent
{
    public required int ObservedPersonCount { get; init; }

    public required int AuthorisedPersonCount { get; init; }

    /// <summary>The pass being used, when the entry was tied to one.</summary>
    public Guid? VisitPassId { get; init; }
}

// ---------------------------------------------------------------------------
// Life safety — these ride the SOS priority lane
// ---------------------------------------------------------------------------

/// <summary>
/// Smoke or flame in a common area. Routed on the SOS lane and never batched: a fire alert
/// queued behind a notice broadcast is a fire alert that arrived too late.
/// </summary>
public sealed record FireOrSmokeDetected : VisionEvent
{
    /// <summary>Smoke or Flame.</summary>
    public required string SignalType { get; init; }
}

/// <summary>
/// A person fell and did not get up within the configured window. Aimed at elderly
/// residents in lobbies, stairwells and gardens.
/// </summary>
public sealed record FallDetected : VisionEvent
{
    public required int MotionlessSeconds { get; init; }
}

/// <summary>An unusual gathering formed, which may precede a dispute or be entirely social.</summary>
public sealed record CrowdGatheringDetected : VisionEvent
{
    public required int PersonCount { get; init; }

    public required int DurationSeconds { get; init; }
}

// Face recognition lives in FaceRecognitionEvents.cs, covering residents, staff, visitors
// and watchlist subjects, along with the template lifecycle and match audit trail.

// ---------------------------------------------------------------------------
// Fleet health
// ---------------------------------------------------------------------------

/// <summary>
/// A camera stopped responding. Worth an alert on its own: a camera that is quietly dead is
/// worse than no camera, because the society believes it is covered.
/// </summary>
public sealed record CameraOffline : IntegrationEvent
{
    public required Guid CameraId { get; init; }

    public required string CameraName { get; init; }

    public required string Zone { get; init; }

    public required DateTimeOffset LastSeenUtc { get; init; }
}

public sealed record CameraRecovered : IntegrationEvent
{
    public required Guid CameraId { get; init; }

    public required TimeSpan OutageDuration { get; init; }
}

/// <summary>
/// An edge box went silent. Every camera behind it is dark, so this escalates above a
/// single camera outage.
/// </summary>
public sealed record EdgeAgentUnreachable : IntegrationEvent
{
    public required string EdgeAgentId { get; init; }

    public required int AffectedCameraCount { get; init; }

    public required DateTimeOffset LastHeartbeatUtc { get; init; }
}
