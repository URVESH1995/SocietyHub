namespace SocietyHub.Contracts.Identity;

public sealed record UserRegistered : IntegrationEvent
{
    public required Guid UserId { get; init; }

    public required string Email { get; init; }

    public required string FullName { get; init; }

    public required string PhoneNumber { get; init; }

    public required IReadOnlyCollection<string> Roles { get; init; }
}
