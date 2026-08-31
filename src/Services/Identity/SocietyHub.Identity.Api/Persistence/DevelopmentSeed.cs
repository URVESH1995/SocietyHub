using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Web.Security;

namespace SocietyHub.Identity.Api.Persistence;

/// <summary>
/// Fixed identifiers for the demo society, shared with the Society service's seed so the two
/// agree about which flat is which. Constants rather than generated values so they can be
/// quoted in documentation and pasted into a request.
/// </summary>
public static class DemoData
{
    public static readonly Guid SocietyId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid TowerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid FlatA101 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid FlatA102 = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static readonly Guid AdminUserId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid ResidentUserId = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    public static readonly Guid GuardUserId = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    public static readonly Guid CommitteeUserId = Guid.Parse("a0000000-0000-0000-0000-000000000004");

    public const string AdminPhone = "+919000000001";
    public const string ResidentPhone = "+919000000002";
    public const string GuardPhone = "+919000000003";
    public const string CommitteePhone = "+919000000004";
}

/// <summary>
/// Creates a usable demo society, and exists because without it a fresh database is a dead
/// end.
///
/// Every write endpoint requires a token; every token requires a user with a membership; and
/// the only way to create one is a write endpoint. There is no self-service sign-up by design
/// — anyone could claim to live in a building — so a real deployment breaks that cycle when
/// the platform team provisions the first society administrator out of band. In development
/// that would mean hand-writing rows before the API could be touched at all.
///
/// Development only, and guarded twice: the caller checks the environment and this refuses to
/// run if any user already exists, so it can never overwrite real data.
/// </summary>
public static class DevelopmentSeed
{
    public static async Task SeedAsync(
        SocietyHubIdentityDbContext context,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (await context.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var people = new (Guid Id, string Phone, string Name, string Role, Guid? FlatId, string? Relationship)[]
        {
            (DemoData.AdminUserId, DemoData.AdminPhone, "Demo Admin",
                SocietyHubRoles.SocietyAdmin, null, null),

            (DemoData.ResidentUserId, DemoData.ResidentPhone, "Amit Sharma",
                SocietyHubRoles.Resident, DemoData.FlatA101, "Owner"),

            (DemoData.GuardUserId, DemoData.GuardPhone, "Ramesh (Gate)",
                SocietyHubRoles.Guard, null, null),

            (DemoData.CommitteeUserId, DemoData.CommitteePhone, "Priya Nair",
                SocietyHubRoles.CommitteeMember, DemoData.FlatA102, "Owner"),
        };

        foreach (var (id, phone, name, role, flatId, relationship) in people)
        {
            context.Users.Add(new ApplicationUser
            {
                Id = id,
                UserName = phone,
                NormalizedUserName = phone.ToUpperInvariant(),
                PhoneNumber = phone,

                // Confirmed, because in this flow the OTP *is* the confirmation and requiring
                // a separate one would leave every seeded account unable to sign in.
                PhoneNumberConfirmed = true,
                FullName = name,
                PreferredLanguage = "en-IN",
                CreatedAtUtc = now,
                SecurityStamp = Guid.CreateVersion7().ToString(),
                ConcurrencyStamp = Guid.CreateVersion7().ToString(),
            });

            context.SocietyMemberships.Add(
                new SocietyMembership(Guid.CreateVersion7(), id, DemoData.SocietyId, role)
                {
                    FlatId = flatId,
                    Relationship = relationship,
                    CreatedAtUtc = now,
                });
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Seeded demo society {SocietyId} with {Count} users. Sign in with {AdminPhone} (admin), " +
            "{ResidentPhone} (resident), {GuardPhone} (guard).",
            DemoData.SocietyId,
            people.Length,
            DemoData.AdminPhone,
            DemoData.ResidentPhone,
            DemoData.GuardPhone);
    }
}
