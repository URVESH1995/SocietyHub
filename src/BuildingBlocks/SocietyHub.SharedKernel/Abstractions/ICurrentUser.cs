namespace SocietyHub.SharedKernel.Abstractions;

/// <summary>The authenticated principal behind the current request.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    string? Email { get; }

    bool IsAuthenticated { get; }

    IReadOnlyCollection<string> Roles { get; }

    bool IsInRole(string role);

    Guid RequireUserId();
}
