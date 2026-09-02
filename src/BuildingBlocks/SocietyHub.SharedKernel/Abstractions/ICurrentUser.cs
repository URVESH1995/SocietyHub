namespace SocietyHub.SharedKernel.Abstractions;

/// <summary>The authenticated principal behind the current request.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    string? Email { get; }

    /// <summary>
    /// The name to show a person, from the token's name claim.
    ///
    /// Separate from Email because most residents sign in by phone and have no email at all —
    /// anything that fell back to it would display nothing, or worse, a placeholder that ends
    /// up stored as the author of a notice.
    /// </summary>
    string? DisplayName { get; }

    bool IsAuthenticated { get; }

    IReadOnlyCollection<string> Roles { get; }

    bool IsInRole(string role);

    Guid RequireUserId();
}
