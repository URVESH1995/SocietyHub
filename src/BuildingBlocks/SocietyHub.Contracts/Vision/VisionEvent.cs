namespace SocietyHub.Contracts.Vision;

/// <summary>
/// Base for anything a camera's on-site analytics reports upward.
///
/// These originate on an edge box inside the society, not in the cloud. Streaming 2,700
/// cameras to Azure would be roughly 5 Gbps sustained and 57 TB a day, so inference runs
/// where the camera is and only the conclusion travels — this payload plus a thumbnail.
/// Footage stays on the local recorder.
///
/// Every detection is therefore a claim made by a model, with a confidence attached.
/// Consumers must treat it as evidence to act on, never as established fact: an alert asks
/// a human to look, it does not decide anything on its own.
/// </summary>
public abstract record VisionEvent : IntegrationEvent
{
    public required Guid CameraId { get; init; }

    /// <summary>Human-readable placement, e.g. "Main Gate — Inbound Lane".</summary>
    public required string CameraName { get; init; }

    /// <summary>Gate, Lobby, Parking, Perimeter, Terrace or CommonArea.</summary>
    public required string Zone { get; init; }

    /// <summary>
    /// When the frame was captured, which is not when it arrived. An edge box that lost its
    /// uplink may forward hours later, and an alert timeline ordered by arrival would be
    /// wrong in exactly the incident where ordering matters most.
    /// </summary>
    public required DateTimeOffset DetectedAtUtc { get; init; }

    /// <summary>Model confidence, 0 to 1. Alert thresholds are configured per society.</summary>
    public required double Confidence { get; init; }

    /// <summary>Short-lived signed URL to a still frame. Never a public link.</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>Identifies the edge deployment, so a misbehaving model can be traced.</summary>
    public required string EdgeAgentId { get; init; }

    /// <summary>Model name and version. A spike in false positives must be attributable.</summary>
    public required string ModelVersion { get; init; }
}
