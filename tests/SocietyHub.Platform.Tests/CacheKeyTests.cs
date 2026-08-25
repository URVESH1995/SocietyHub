using SocietyHub.Caching;

namespace SocietyHub.Platform.Tests;

/// <summary>
/// The cache is where tenant isolation can be undone without touching the database — no query
/// filter, interceptor or row-level security policy sits anywhere near a Redis GET. These
/// assert that a tenant-scoped key cannot be built without a real society.
/// </summary>
public sealed class CacheKeyTests
{
    private static readonly Guid SocietyA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SocietyB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void Two_societies_never_share_a_key_for_the_same_logical_item()
    {
        // The leak this type exists to prevent: both societies have a flat A-101, and an
        // unscoped key would serve one society's data to the other.
        var a = CacheKey.ForSociety(SocietyA, "flat", "A-101");
        var b = CacheKey.ForSociety(SocietyB, "flat", "A-101");

        Assert.NotEqual(a.Value, b.Value);
        Assert.Contains(SocietyA.ToString("N"), a.Value);
        Assert.Contains(SocietyB.ToString("N"), b.Value);
    }

    [Fact]
    public void An_empty_society_is_refused()
    {
        // Guid.Empty is what the tenant context yields for a request with no society. Allowing
        // it would create one shared bucket every tenant-less request reads and writes.
        var ex = Assert.Throws<ArgumentException>(
            () => CacheKey.ForSociety(Guid.Empty, "flat", "A-101"));

        Assert.Contains("society", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_category_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CacheKey.ForSociety(SocietyA, "  "));
    }

    [Fact]
    public void The_society_prefix_matches_that_societys_keys_and_no_others()
    {
        var prefix = CacheKey.SocietyPrefix(SocietyA);

        Assert.StartsWith(prefix, CacheKey.ForSociety(SocietyA, "flat", "A-101").Value, StringComparison.Ordinal);
        Assert.StartsWith(prefix, CacheKey.ForSociety(SocietyA, "settings").Value, StringComparison.Ordinal);
        Assert.DoesNotContain(prefix, CacheKey.ForSociety(SocietyB, "flat", "A-101").Value);
    }

    [Fact]
    public void Platform_wide_keys_are_distinguishable_from_tenant_keys()
    {
        var global = CacheKey.ForPlatformWideData("service-catalogue");

        Assert.DoesNotContain(":t:", global.Value);
        Assert.Contains(":global:", global.Value);
    }

    [Fact]
    public void Key_parts_compose_in_order()
    {
        var key = CacheKey.ForSociety(SocietyA, "visitor", "pass", "1234");

        Assert.EndsWith("visitor:pass:1234", key.Value, StringComparison.Ordinal);
    }
}
