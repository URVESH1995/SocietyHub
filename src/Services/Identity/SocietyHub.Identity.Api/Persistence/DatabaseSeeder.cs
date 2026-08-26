using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Web.Security;

namespace SocietyHub.Identity.Api.Persistence;

/// <summary>
/// Applies migrations and ensures the seven roles exist as reference rows.
///
/// A note on why these rows are reference data rather than the authority: a role here is held
/// <em>within a society</em>, so the authoritative value lives on
/// <see cref="SocietyMembership.Role"/>. ASP.NET Identity's role tables are global and cannot
/// express "Resident in one society, CommitteeMember in another". They are kept because
/// admin screens need a list of valid roles to offer, and a foreign key would otherwise be
/// tempting and wrong.
/// </summary>
public static class DatabaseSeeder
{
    private static readonly Dictionary<string, string> RoleDescriptions = new()
    {
        [SocietyHubRoles.SuperAdmin] = "Platform operator. Spans societies, always audited.",
        [SocietyHubRoles.SocietyAdmin] = "Runs one society: flats, guards, settings.",
        [SocietyHubRoles.CommitteeMember] = "Elected committee. Notices, escalations, drives.",
        [SocietyHubRoles.Resident] = "Owner or tenant of a flat.",
        [SocietyHubRoles.Guard] = "Security staff working from a shared gate device.",
        [SocietyHubRoles.Vendor] = "A service company fulfilling bulk drives.",
        [SocietyHubRoles.Technician] = "An individual sent by a vendor to perform a job.",
    };

    public static async Task MigrateAndSeedAsync(
        SocietyHubIdentityDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Fine for development and a single-instance deploy. Production applies migrations as
        // a separate gated step — several replicas starting at once would otherwise race, and
        // an automatic migration on boot is how an unreviewed schema change reaches production.
        await context.Database.MigrateAsync(cancellationToken);

        var existing = await context.Roles
            .Select(r => r.Name!)
            .ToListAsync(cancellationToken);

        var missing = RoleDescriptions
            .Where(pair => !existing.Contains(pair.Key))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        foreach (var (role, description) in missing)
        {
            context.Roles.Add(new ApplicationRole
            {
                Id = Guid.CreateVersion7(),
                Name = role,
                NormalizedName = role.ToUpperInvariant(),
                Description = description,
                ConcurrencyStamp = Guid.CreateVersion7().ToString(),
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded {Count} role definitions.", missing.Count);
    }
}
