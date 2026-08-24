using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Primitives;

namespace SocietyHub.Persistence;

/// <summary>
/// Base context for every service that stores society-scoped data.
///
/// Layer one of tenant isolation: any entity implementing <see cref="ITenantScoped"/> is
/// discovered during model building and given a filter automatically. There is no per-entity
/// registration step, so a developer adding a new table cannot forget it — the only way to
/// opt out is to not implement the interface, which is a visible, reviewable decision.
/// </summary>
public abstract class TenantDbContext : DbContext
{
    /// <summary>Names let the two filters coexist; a single unnamed filter would overwrite.</summary>
    public const string TenantFilterName = "SocietyHub:Tenant";

    public const string SoftDeleteFilterName = "SocietyHub:SoftDelete";

    private static readonly MethodInfo ConfigureTenantFilterMethod =
        typeof(TenantDbContext).GetMethod(
            nameof(ConfigureTenantFilter),
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ConfigureSoftDeleteFilterMethod =
        typeof(TenantDbContext).GetMethod(
            nameof(ConfigureSoftDeleteFilter),
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    protected TenantDbContext(DbContextOptions options, ITenantContext tenantContext)
        : base(options) => TenantContext = tenantContext;

    protected ITenantContext TenantContext { get; }

    /// <summary>
    /// Read by the compiled filter on every query, so it reflects the current request rather
    /// than whatever was in scope when the model was first built.
    ///
    /// Falls back to <see cref="Guid.Empty"/>, which matches no row. An unauthenticated or
    /// tenant-less request therefore sees nothing at all: the default is deny, not deny-if-
    /// someone-remembered-to-check.
    /// </summary>
    public Guid ActiveSocietyId => TenantContext.SocietyId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            // Derived types in a hierarchy inherit the root's filter, and owned types are
            // reached only through their owner, which is already filtered.
            if (entityType.BaseType is not null || entityType.IsOwned())
            {
                continue;
            }

            var clrType = entityType.ClrType;

            if (typeof(ITenantScoped).IsAssignableFrom(clrType))
            {
                ConfigureTenantFilterMethod
                    .MakeGenericMethod(clrType)
                    .Invoke(this, [modelBuilder]);
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                ConfigureSoftDeleteFilterMethod
                    .MakeGenericMethod(clrType)
                    .Invoke(this, [modelBuilder]);
            }
        }
    }

    /// <summary>
    /// Deliberately a plain equality rather than an <c>IsPlatformScope || …</c> disjunction.
    /// A disjunction would emit <c>WHERE @scope = 1 OR SocietyId = @id</c>, which is not
    /// sargable and costs the index on every query for the benefit of a rare support path.
    /// Platform-scope reads call <c>IgnoreQueryFilters()</c> explicitly instead, which is
    /// both faster and far easier to audit.
    /// </summary>
    private void ConfigureTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped =>
        modelBuilder.Entity<TEntity>()
                    .HasQueryFilter(TenantFilterName, e => e.SocietyId == ActiveSocietyId);

    private void ConfigureSoftDeleteFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ISoftDeletable =>
        modelBuilder.Entity<TEntity>()
                    .HasQueryFilter(SoftDeleteFilterName, e => !e.IsDeleted);
}
