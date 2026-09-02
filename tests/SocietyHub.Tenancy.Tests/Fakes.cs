using SocietyHub.SharedKernel.Abstractions;

namespace SocietyHub.Tenancy.Tests;

internal sealed class FakeTenantContext : ITenantContext
{
    public Guid? SocietyId { get; set; }

    public bool IsPlatformScope { get; set; }

    public Guid RequireSocietyId() =>
        SocietyId ?? throw new InvalidOperationException("No society in scope.");
}

internal sealed class FakeCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public string? Email => "resident@societyhub.test";

    public string? DisplayName { get; set; } = "Amit Sharma";

    public bool IsAuthenticated => UserId is not null;

    public IReadOnlyCollection<string> Roles { get; set; } = ["Resident"];

    public bool IsInRole(string role) => Roles.Contains(role);

    public Guid RequireUserId() => UserId ?? throw new InvalidOperationException("No user.");
}
