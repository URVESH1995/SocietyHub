namespace SocietyHub.Caching;

/// <summary>
/// Builds cache keys that carry the society they belong to.
///
/// The cache is the one place tenant isolation can be undone without touching the database.
/// A key like <c>flat:A-101</c> looks harmless and is a cross-society data leak the moment
/// two societies both have a flat A-101 — and unlike a missing <c>WHERE</c> clause, no query
/// filter, interceptor or row-level security policy is anywhere near it.
///
/// So a tenant-scoped key cannot be constructed without a society id. Making that a compile
/// time requirement rather than a convention is the entire purpose of this type.
/// </summary>
public readonly record struct CacheKey
{
    private const string Prefix = "sh";

    private CacheKey(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// A key scoped to one society: <c>sh:t:{societyId}:{category}:{id}</c>.
    /// </summary>
    public static CacheKey ForSociety(Guid societyId, string category, params string[] parts)
    {
        if (societyId == Guid.Empty)
        {
            // Guid.Empty is what the tenant context yields when there is no society on the
            // request. Allowing it here would create one shared bucket that every tenant-less
            // request reads and writes — precisely the leak this type exists to prevent.
            throw new ArgumentException(
                "A tenant-scoped cache key requires a real society.", nameof(societyId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var suffix = parts.Length == 0 ? string.Empty : ":" + string.Join(':', parts);

        return new CacheKey($"{Prefix}:t:{societyId:N}:{category}{suffix}");
    }

    /// <summary>
    /// Everything cached for one society, for invalidating a tenant wholesale.
    /// </summary>
    public static string SocietyPrefix(Guid societyId) => $"{Prefix}:t:{societyId:N}:";

    /// <summary>
    /// A key for genuinely global data — the service catalogue, platform reference tables.
    ///
    /// Named awkwardly on purpose. Reaching for it should feel like a decision, because
    /// anything society-specific placed here is visible to every society on the platform.
    /// </summary>
    public static CacheKey ForPlatformWideData(string category, params string[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var suffix = parts.Length == 0 ? string.Empty : ":" + string.Join(':', parts);

        return new CacheKey($"{Prefix}:global:{category}{suffix}");
    }

    public override string ToString() => Value;

    public static implicit operator string(CacheKey key) => key.Value;
}
