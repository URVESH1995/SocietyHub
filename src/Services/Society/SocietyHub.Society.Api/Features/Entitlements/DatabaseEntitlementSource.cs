using Microsoft.EntityFrameworkCore;
using SocietyHub.Features;
using SocietyHub.Society.Api.Persistence;

namespace SocietyHub.Society.Api.Features.Entitlements;

/// <summary>
/// The authoritative entitlement source, used only inside the Society service.
///
/// Everywhere else reads <see cref="CachedEntitlementSource"/>. The owner reads its own tables
/// so that an operator disabling a feature and then immediately checking it does not get a
/// stale answer from a cache it just wrote.
/// </summary>
public sealed class DatabaseEntitlementSource : IEntitlementSource
{
    private readonly SocietyDbContext _context;

    public DatabaseEntitlementSource(SocietyDbContext context) => _context = context;

    public async Task<SocietyEntitlements?> GetAsync(
        Guid societyId, CancellationToken cancellationToken = default)
    {
        var subscription = await _context.Subscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SocietyId == societyId, cancellationToken);

        // A society with no subscription row is not an error — it is a society nobody has put
        // on a plan yet, and the honest answer for it is Basic.
        return subscription?.ToSnapshot() ?? SocietyEntitlements.Fallback(societyId);
    }

    public async Task<FeatureRolloutMap> GetRolloutsAsync(
        CancellationToken cancellationToken = default) =>
        new(await _context.FeatureRollouts
            .AsNoTracking()
            .Select(r => r.ToRollout())
            .ToListAsync(cancellationToken));
}
