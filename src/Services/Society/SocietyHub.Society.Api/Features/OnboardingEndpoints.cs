using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Globalization;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Society.Api.Domain;
using SocietyHub.Society.Api.Persistence;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Society.Api.Features;

public sealed record CreateSocietyRequest(
    Guid SocietyId,
    string Name,
    string? RegistrationNumber,
    string? City,
    string? State,
    string DefaultLanguage,
    string TimeZoneId,
    string Currency,
    string CountryCode);

/// <summary>One row of a bulk flat import.</summary>
public sealed record FlatImportRow(string TowerName, string FlatNumber, int FloorNumber, string FlatType);

public sealed record ImportFlatsRequest(IReadOnlyList<FlatImportRow> Flats);

public sealed record ImportFlatsResponse(int TowersCreated, int FlatsCreated, IReadOnlyList<string> Skipped);

public sealed class CreateSocietyValidator : AbstractValidator<CreateSocietyRequest>
{
    public CreateSocietyValidator()
    {
        RuleFor(r => r.SocietyId).NotEmpty().WithErrorCode("Society.IdRequired");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(200).WithErrorCode("Society.NameRequired");

        RuleFor(r => r.DefaultLanguage)
            .Must(l => LanguageTag.Create(l).IsSuccess)
            .WithErrorCode("Language.Unsupported");

        RuleFor(r => r.Currency).Length(3).WithErrorCode("Currency.Invalid");
        RuleFor(r => r.CountryCode).Length(2).WithErrorCode("Country.Invalid");

        RuleFor(r => r.TimeZoneId)
            .Must(BeAKnownTimeZone)
            .WithErrorCode("TimeZone.Unknown")
            .WithMessage("Not a recognised IANA time zone.");
    }

    /// <summary>
    /// Validated at creation rather than trusted, because the SLA clock, escalation windows
    /// and quiet hours are all judged in this zone. A typo here silently mis-times every
    /// complaint deadline in the society.
    /// </summary>
    private static bool BeAKnownTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

public sealed class ImportFlatsValidator : AbstractValidator<ImportFlatsRequest>
{
    public ImportFlatsValidator()
    {
        RuleFor(r => r.Flats).NotEmpty().WithErrorCode("Import.Empty");

        // Bounded so one request cannot hold a transaction open across a whole township.
        RuleFor(r => r.Flats.Count).LessThanOrEqualTo(2000).WithErrorCode("Import.TooLarge");

        RuleForEach(r => r.Flats).ChildRules(flat =>
        {
            flat.RuleFor(f => f.TowerName).NotEmpty().WithErrorCode("Tower.NameRequired");
            flat.RuleFor(f => f.FlatNumber).NotEmpty().WithErrorCode("Flat.NumberRequired");
            flat.RuleFor(f => f.FloorNumber).GreaterThanOrEqualTo(-5).WithErrorCode("Flat.FloorInvalid");
            flat.RuleFor(f => f.FlatType).NotEmpty().WithErrorCode("Flat.TypeRequired");
        });
    }
}

/// <summary>
/// Bringing a society onto the platform.
///
/// Onboarding is the moment most of these deployments succeed or stall: a committee has a
/// spreadsheet of 250 flats and no appetite for typing them in one at a time. The bulk import
/// is therefore the feature, and it is deliberately forgiving — a duplicate row is skipped and
/// reported rather than failing the whole upload, because a real spreadsheet always has some.
/// </summary>
public static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/societies").WithTags("Onboarding");

        group.MapPost("/", CreateAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithValidation<CreateSocietyRequest>()
             .WithSummary("Registers a society. Platform operators only.");

        group.MapPost("/current/flats/import", ImportFlatsAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithValidation<ImportFlatsRequest>()
             .WithSummary("Bulk-creates towers and flats from a spreadsheet export.");

        group.MapGet("/current", GetCurrentAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Returns the society in scope, with its settings.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateSocietyRequest request,
        SocietyDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // Platform scope, so the filter is bypassed deliberately — the society being created
        // does not exist yet and no request could be scoped to it.
        var exists = await context.Societies
            .IgnoreQueryFilters()
            .AnyAsync(s => s.Id == request.SocietyId, cancellationToken);

        if (exists)
        {
            return Result
                .Failure(Error.Conflict("Society.Exists", "That society is already registered."))
                .ToProblem();
        }

        // The id is supplied rather than generated so it matches the tenant identifier the
        // Identity service already issues in tokens. Two ids for one society would be a
        // permanent mapping table nobody wants.
        var society = new Domain.Society(
            request.SocietyId,
            request.Name,
            new SocietySettings(
                request.DefaultLanguage,
                request.TimeZoneId,
                request.Currency.ToUpperInvariant(),
                request.CountryCode.ToUpperInvariant()))
        {
            RegistrationNumber = request.RegistrationNumber,
            City = request.City,
            State = request.State,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };

        context.Societies.Add(society);
        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/societies/{society.Id}", new { society.Id, society.Name });
    }

    private static async Task<IResult> ImportFlatsAsync(
        ImportFlatsRequest request,
        SocietyDbContext context,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();

        var society = await context.Societies
            .Include(s => s.Towers)
            .ThenInclude(t => t.Flats)
            .SingleOrDefaultAsync(s => s.Id == societyId, cancellationToken);

        if (society is null)
        {
            return Result
                .Failure(Error.NotFound("Society.NotFound", "This society is not registered."))
                .ToProblem();
        }

        var skipped = new List<string>();
        var towersCreated = 0;
        var flatsCreated = 0;

        foreach (var row in request.Flats)
        {
            var tower = society.Towers.FirstOrDefault(
                t => string.Equals(t.Name, row.TowerName, StringComparison.OrdinalIgnoreCase));

            if (tower is null)
            {
                tower = society.AddTower(row.TowerName);
                towersCreated++;
            }

            try
            {
                tower.AddFlat(row.FlatNumber, row.FloorNumber, row.FlatType);
                flatsCreated++;
            }
            catch (InvalidOperationException)
            {
                // A duplicate in the spreadsheet. Reported rather than fatal — rejecting a
                // 250-row upload over one repeated line is how onboarding stalls.
                skipped.Add($"{row.TowerName}/{row.FlatNumber}");
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(
            new ImportFlatsResponse(towersCreated, flatsCreated, skipped));
    }

    private static async Task<IResult> GetCurrentAsync(
        SocietyDbContext context,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var society = await context.Societies
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == tenant.RequireSocietyId(), cancellationToken);

        return society is null
            ? Result.Failure(Error.NotFound("Society.NotFound", "Not found.")).ToProblem()
            : Microsoft.AspNetCore.Http.Results.Ok(new
            {
                society.Id,
                society.Name,
                society.City,
                society.State,
                settings = new
                {
                    society.Settings.DefaultLanguage,
                    society.Settings.TimeZoneId,
                    society.Settings.Currency,
                    society.Settings.CountryCode,
                },
            });
    }
}
