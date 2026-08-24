namespace SocietyHub.Contracts.Society;

public sealed record FlatOccupancyChanged : IntegrationEvent
{
    public required Guid FlatId { get; init; }

    /// <summary>Vacant, OwnerOccupied or Rented.</summary>
    public required string Occupancy { get; init; }

    public required string FlatNumber { get; init; }

    public required string TowerName { get; init; }
}
