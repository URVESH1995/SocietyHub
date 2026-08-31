using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Persistence.Interceptors;

/// <summary>
/// Layer two of tenant isolation, and the layer most systems are missing.
///
/// A global query filter constrains what a <c>SELECT</c> returns. It does nothing whatsoever
/// about an <c>INSERT</c> or an <c>UPDATE</c>. Without this interceptor, a request that can
/// influence a <see cref="ITenantScoped.SocietyId"/> — a bound model, a mapper copying a
/// client-supplied field, a handler that trusts its input — writes straight into another
/// society's data, and every read-side test still passes.
///
/// So every pending write is checked here, immediately before it reaches the database:
/// inserts are stamped with the caller's society, and anything already carrying a different
/// one is refused.
/// </summary>
public sealed class TenantGuardInterceptor : SaveChangesInterceptor
{
    private const string SocietyIdProperty = nameof(ITenantScoped.SocietyId);

    private readonly ITenantContext _tenant;

    public TenantGuardInterceptor(ITenantContext tenant) => _tenant = tenant;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Guard(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // Platform operators legitimately write across societies — data migrations, support
        // tooling, onboarding. Reaching this branch requires an authorisation policy on the
        // endpoint, so the bypass is always deliberate and always auditable.
        if (_tenant.IsPlatformScope)
        {
            return;
        }

        var currentSocietyId = _tenant.SocietyId;

        foreach (var entry in context.ChangeTracker.Entries<ITenantScoped>())
        {
            // The society aggregate is its own tenant: its SocietyId is a computed `=> Id`
            // with no mapped column, so there is nothing to stamp and nothing to rewrite.
            // Its tenancy is intrinsic and set at construction, which is a different problem
            // from the one this interceptor exists to solve.
            var isIntrinsicallyScoped =
                entry.Metadata.FindProperty(SocietyIdProperty) is null;

            switch (entry.State)
            {
                case EntityState.Added when isIntrinsicallyScoped:
                    RejectIntrinsicMismatch(entry, currentSocietyId);
                    break;

                case EntityState.Added:
                    StampOrReject(entry, currentSocietyId);
                    break;

                case EntityState.Modified when isIntrinsicallyScoped:
                case EntityState.Deleted when isIntrinsicallyScoped:
                    RejectIntrinsicMismatch(entry, currentSocietyId);
                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    RejectForeignRow(entry, currentSocietyId);
                    break;

                case EntityState.Detached:
                case EntityState.Unchanged:
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Checks an entity whose tenancy is intrinsic rather than stored — currently only the
    /// society aggregate, whose <c>SocietyId</c> is its own <c>Id</c>.
    ///
    /// There is nothing to stamp: the value came from the constructor and cannot be rewritten.
    /// So this only refuses the case that matters, a request scoped to one society touching
    /// another society's row. A request with no tenant at all is allowed through, because
    /// creating a society necessarily happens before any tenant exists — and that path is
    /// reachable only from onboarding, which sits behind a platform-scope policy.
    /// </summary>
    private static void RejectIntrinsicMismatch(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<ITenantScoped> entry,
        Guid? currentSocietyId)
    {
        if (currentSocietyId is null)
        {
            return;
        }

        if (entry.Entity.SocietyId != currentSocietyId)
        {
            throw new TenantIsolationViolationException(
                entry.Metadata.DisplayName(), entry.Entity.SocietyId, currentSocietyId);
        }
    }

    private static void StampOrReject(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<ITenantScoped> entry,
        Guid? currentSocietyId)
    {
        var property = entry.Property(SocietyIdProperty);
        var assigned = (Guid)property.CurrentValue!;

        // The normal path: the handler left it unset and we stamp it. Handlers are not
        // expected to know the tenant, which is precisely why they cannot get it wrong.
        if (assigned == Guid.Empty)
        {
            if (currentSocietyId is null)
            {
                throw new TenantIsolationViolationException(
                    entry.Metadata.DisplayName(), Guid.Empty, null);
            }

            property.CurrentValue = currentSocietyId.Value;
            return;
        }

        if (assigned != currentSocietyId)
        {
            throw new TenantIsolationViolationException(
                entry.Metadata.DisplayName(), assigned, currentSocietyId);
        }
    }

    private static void RejectForeignRow(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<ITenantScoped> entry,
        Guid? currentSocietyId)
    {
        var property = entry.Property(SocietyIdProperty);

        // The row as it exists in the database. Checking the original rather than the
        // current value stops an attacker rewriting SocietyId to their own on the way past.
        var owner = (Guid)property.OriginalValue!;

        if (owner != currentSocietyId)
        {
            throw new TenantIsolationViolationException(
                entry.Metadata.DisplayName(), owner, currentSocietyId);
        }

        // A row never changes hands. Moving a flat between societies is an offline,
        // platform-scope operation, not something a normal request may do.
        if (entry.State == EntityState.Modified
            && property.IsModified
            && (Guid)property.CurrentValue! != owner)
        {
            throw new TenantIsolationViolationException(
                entry.Metadata.DisplayName(), (Guid)property.CurrentValue!, currentSocietyId);
        }
    }
}
