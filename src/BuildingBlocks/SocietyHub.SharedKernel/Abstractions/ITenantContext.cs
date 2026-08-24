namespace SocietyHub.SharedKernel.Abstractions;

/// <summary>
/// Resolves which society the current request belongs to. Populated from the
/// <c>society_id</c> JWT claim, and consumed by every DbContext global query filter.
/// </summary>
public interface ITenantContext
{
    /// <summary>The society in scope, or <see langword="null"/> for platform-level calls.</summary>
    Guid? SocietyId { get; }

    /// <summary>
    /// True for platform operators who legitimately span societies. Such calls bypass the
    /// query filter, so every use must be behind an explicit authorisation policy.
    /// </summary>
    bool IsPlatformScope { get; }

    /// <summary>The society in scope, or throws when a tenant-bound operation has none.</summary>
    Guid RequireSocietyId();
}
