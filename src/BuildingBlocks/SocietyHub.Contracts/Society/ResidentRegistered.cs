namespace SocietyHub.Contracts.Society;

public sealed record ResidentRegistered : IntegrationEvent
{
    public required Guid ResidentId { get; init; }

    public required Guid FlatId { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>Owner, Tenant or FamilyMember.</summary>
    public required string Relationship { get; init; }

    public required bool IsPrimaryContact { get; init; }
}
