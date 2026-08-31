using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocietyHub.Society.Api.Domain;

namespace SocietyHub.Society.Api.Persistence;

/// <summary>
/// Creates the demo society, tower and flats that the Identity service's seeded users are
/// members of.
///
/// The identifiers are duplicated as constants here rather than shared through a common
/// project, and that is deliberate: services do not share a database or a domain assembly,
/// and introducing one just for seed data would be the first crack in that boundary. Two
/// short const blocks that must agree is the honest cost of the separation.
///
/// Development only, and refuses to run if any society already exists.
/// </summary>
public static class DevelopmentSeed
{
    private static readonly Guid SocietyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TowerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FlatA101 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid FlatA102 = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static async Task SeedAsync(
        SocietyDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters because this runs at startup with no tenant on the request, so
        // the filter would report an empty database and seed a duplicate on every boot.
        if (await context.Societies.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var society = new Domain.Society(
            SocietyId,
            "Green Meadows Residency",
            new SocietySettings("en-IN", "Asia/Kolkata", "INR", "IN"))
        {
            City = "Pune",
            AddressLine1 = "Baner Road",
            CreatedAtUtc = now,
        };

        var tower = new Tower(TowerId, SocietyId, "A") { FloorCount = 12 };

        context.Societies.Add(society);
        context.Towers.Add(tower);

        // Constructed directly rather than through Tower.AddFlat, because the ids have to
        // match what Identity seeded its memberships against.
        context.Flats.Add(new Flat(FlatA101, SocietyId, TowerId, "A-101", 1, "2BHK")
        {
            CreatedAtUtc = now,
        });

        context.Flats.Add(new Flat(FlatA102, SocietyId, TowerId, "A-102", 1, "3BHK")
        {
            CreatedAtUtc = now,
        });

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded demo society {SocietyId} ('{Name}') with tower A and flats A-101, A-102.",
            SocietyId,
            society.Name);
    }
}
