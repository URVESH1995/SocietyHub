namespace SocietyHub.Contracts.Gate;

/// <summary>A resident approved a visitor ahead of arrival; the gate can admit on OTP.</summary>
public sealed record VisitorPreApproved : IntegrationEvent
{
    public required Guid VisitPassId { get; init; }

    public required Guid FlatId { get; init; }

    public required string VisitorName { get; init; }

    public required string VisitorPhone { get; init; }

    /// <summary>Guest, Delivery, Cab, Vendor or Staff.</summary>
    public required string VisitorType { get; init; }

    public required DateTimeOffset ValidFromUtc { get; init; }

    public required DateTimeOffset ValidUntilUtc { get; init; }
}

/// <summary>A visitor passed the gate inward. Drives the resident's arrival notification.</summary>
public sealed record VisitorCheckedIn : IntegrationEvent
{
    public required Guid VisitPassId { get; init; }

    public required Guid FlatId { get; init; }

    public required string VisitorName { get; init; }

    public required string VisitorType { get; init; }

    public required DateTimeOffset CheckedInAtUtc { get; init; }

    public required Guid CheckedInByGuardId { get; init; }

    public string? PhotoUrl { get; init; }

    public string? VehicleNumber { get; init; }
}

/// <summary>A visitor passed the gate outward, closing the pass.</summary>
public sealed record VisitorCheckedOut : IntegrationEvent
{
    public required Guid VisitPassId { get; init; }

    public required Guid FlatId { get; init; }

    public required DateTimeOffset CheckedOutAtUtc { get; init; }
}

/// <summary>
/// A resident triggered the panic button. Consumers must treat this as the highest
/// notification priority and fan out to guards, committee and neighbouring flats.
/// </summary>
public sealed record SosRaised : IntegrationEvent
{
    public required Guid IncidentId { get; init; }

    public required Guid FlatId { get; init; }

    public required Guid RaisedByUserId { get; init; }

    /// <summary>Medical, Fire, Security or Other.</summary>
    public required string Category { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }
}
