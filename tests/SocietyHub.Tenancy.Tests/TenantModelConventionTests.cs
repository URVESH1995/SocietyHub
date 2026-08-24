using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SocietyHub.Persistence;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Tenancy.Tests;

/// <summary>
/// Layer three: guards the model itself rather than any particular query.
///
/// The isolation tests prove the mechanism works for the tables that exist today. These
/// prove that a table added next month cannot quietly opt out of it. That is the realistic
/// failure — nobody bypasses tenant isolation on purpose, they add an entity and forget.
/// </summary>
public sealed class TenantModelConventionTests : IDisposable
{
    /// <summary>
    /// Entities that legitimately belong to no society: platform reference data, shared
    /// catalogues, outbox plumbing.
    ///
    /// Adding a name here is the explicit, reviewable act of declaring a table global. The
    /// test fails until someone does so, which is the entire point — the decision surfaces
    /// in a pull request instead of in a breach report.
    /// </summary>
    private static readonly HashSet<string> IntentionallyGlobalEntities =
    [
        nameof(ServiceCatalogEntry),
    ];

    private readonly SqliteConnection _connection;

    public TenantModelConventionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    [Fact]
    public void Every_entity_is_either_society_scoped_or_explicitly_declared_global()
    {
        using var context = CreateContext();

        var offenders = RootEntityTypes(context)
            .Where(e => !typeof(ITenantScoped).IsAssignableFrom(e.ClrType))
            .Select(e => e.ClrType.Name)
            .Where(name => !IntentionallyGlobalEntities.Contains(name))
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These entities are neither society-scoped nor declared global: " +
            $"{string.Join(", ", offenders)}. Implement ITenantScoped, or add the name to " +
            $"{nameof(IntentionallyGlobalEntities)} with a reason.");
    }

    [Fact]
    public void Every_society_scoped_entity_has_the_tenant_query_filter_applied()
    {
        using var context = CreateContext();

        var unfiltered = RootEntityTypes(context)
            .Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType))
            .Where(e => !HasFilterNamed(e, TenantDbContext.TenantFilterName))
            .Select(e => e.ClrType.Name)
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            unfiltered.Count == 0,
            $"These society-scoped entities have no tenant query filter: " +
            $"{string.Join(", ", unfiltered)}.");
    }

    [Fact]
    public void Every_soft_deletable_entity_has_the_soft_delete_filter_applied()
    {
        using var context = CreateContext();

        var unfiltered = RootEntityTypes(context)
            .Where(e => typeof(ISoftDeletable).IsAssignableFrom(e.ClrType))
            .Where(e => !HasFilterNamed(e, TenantDbContext.SoftDeleteFilterName))
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.Empty(unfiltered);
    }

    [Fact]
    public void Tenant_and_soft_delete_filters_coexist_rather_than_overwrite()
    {
        // Named filters are the reason this holds. With EF's older single unnamed filter,
        // registering soft-delete would silently replace tenant isolation — which fails
        // open, and is exactly the kind of bug that never shows up in a feature test.
        using var context = CreateContext();

        var visitorLog = context.Model.FindEntityType(typeof(VisitorLog))!;

        Assert.True(HasFilterNamed(visitorLog, TenantDbContext.TenantFilterName));
        Assert.True(HasFilterNamed(visitorLog, TenantDbContext.SoftDeleteFilterName));
    }

    private static IEnumerable<IEntityType> RootEntityTypes(DbContext context) =>
        context.Model.GetEntityTypes().Where(e => e.BaseType is null && !e.IsOwned());

    private static bool HasFilterNamed(IEntityType entityType, string filterName) =>
        entityType.GetDeclaredQueryFilters().Any(f => f.Key == filterName);

    private TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new TestDbContext(options, new FakeTenantContext());
    }

    public void Dispose() => _connection.Dispose();
}
