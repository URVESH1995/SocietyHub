using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Drives;
using SocietyHub.Drives.Api.Domain;
using SocietyHub.Drives.Api.Persistence;
using SocietyHub.Features;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Features;
using SocietyHub.SharedKernel.Globalization;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Drives.Api.Features;

public sealed record OpenDriveRequest(
    string ServiceCode,
    Guid VendorId,
    Guid RateCardId,
    int Quorum,
    int? Capacity,
    DateTimeOffset CutOffAtUtc,
    DateTimeOffset ServiceDateUtc);

public sealed record EnrolRequest(Guid FlatId, int Units);

public sealed record CatalogueItemView(
    string Code, string Name, string UnitLabel, string Category, int SuggestedQuorum);

public sealed record DriveView(
    Guid Id,
    string ServiceCode,
    string Status,
    Guid VendorId,
    int Participants,
    int Units,
    int Quorum,
    int? Capacity,
    int ParticipantsToQuorum,
    bool QuorumReached,
    DateTimeOffset? CutOffAtUtc,
    DateTimeOffset? ServiceDateUtc,
    long? FinalUnitPricePaise);

public sealed class OpenDriveValidator : AbstractValidator<OpenDriveRequest>
{
    public OpenDriveValidator()
    {
        RuleFor(r => r.ServiceCode).NotEmpty().WithErrorCode("Drive.ServiceRequired");

        RuleFor(r => r.Quorum)
            .GreaterThan(0).WithErrorCode("Drive.QuorumRequired")
            .LessThanOrEqualTo(1000).WithErrorCode("Drive.QuorumImplausible");

        RuleFor(r => r.Capacity)
            .GreaterThan(0).When(r => r.Capacity is not null)
            .WithErrorCode("Drive.BadCapacity");
    }
}

public sealed class EnrolValidator : AbstractValidator<EnrolRequest>
{
    public EnrolValidator()
    {
        RuleFor(r => r.FlatId).NotEmpty().WithErrorCode("Drive.FlatRequired");

        RuleFor(r => r.Units)
            .InclusiveBetween(1, 50).WithErrorCode("Drive.BadUnits");
    }
}

public static class DriveEndpoints
{
    public static IEndpointRouteBuilder MapDriveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/drives").WithTags("Drives");

        group.MapGet("/catalogue", CatalogueAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithSummary("Services a drive can be opened for, in the caller's language.");

        // Opening commits the society to a vendor and starts taking residents' money, so it is
        // a committee decision rather than something any resident can trigger.
        group.MapPost("/", OpenAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithValidation<OpenDriveRequest>()
             .WithSummary("Opens a drive and starts accepting enrolments.");

        group.MapGet("/", ListAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithSummary("Open and recently closed drives for this society.");

        group.MapGet("/{id:guid}", DetailAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithSummary("One drive, with its live counter.");

        group.MapPost("/{id:guid}/enrol", EnrolAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithValidation<EnrolRequest>()
             .WithSummary("Joins a drive at the price for the resulting count.");

        group.MapPost("/{id:guid}/withdraw", WithdrawAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithSummary("Leaves a drive before cut-off. Refunded in full.");

        return app;
    }

    private static async Task<IResult> CatalogueAsync(
        DrivesDbContext context,
        ILocaleContext locale,
        CancellationToken cancellationToken)
    {
        var items = await context.Catalogue
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(i => i.IsActive)
            .OrderBy(i => i.Category)
            .ThenBy(i => i.NameEn)
            .ToListAsync(cancellationToken);

        var language = locale.Language.Value;

        return Results.Ok(items.Select(i => new CatalogueItemView(
            i.Code,
            i.NameFor(language),
            i.UnitLabelFor(language),
            i.Category.ToString(),
            i.SuggestedQuorum)));
    }

    private static async Task<IResult> OpenAsync(
        OpenDriveRequest request,
        DrivesDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var drive = new ServiceDrive(
            Guid.CreateVersion7(),
            tenant.RequireSocietyId(),
            request.ServiceCode,
            request.VendorId,
            request.RateCardId,
            currentUser.RequireUserId(),
            request.Quorum,
            now);

        var opened = drive.Open(
            now, request.CutOffAtUtc, request.ServiceDateUtc, request.Capacity);

        if (opened.IsFailure)
        {
            return opened.ToProblem();
        }

        context.Drives.Add(drive);

        outbox.Enqueue(new DriveOpened
        {
            SocietyId = drive.SocietyId,
            DriveId = drive.Id,
            ServiceCode = drive.ServiceCode,
            VendorId = drive.VendorId,
            Quorum = drive.Quorum,
            CutOffAtUtc = request.CutOffAtUtc,
            ServiceDateUtc = request.ServiceDateUtc,
            OccurredAtUtc = now,
        });

        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/drives/{drive.Id}", new { id = drive.Id });
    }

    private static async Task<IResult> ListAsync(
        DrivesDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-30);

        var drives = await context.Drives
            .AsNoTracking()
            .Include(d => d.Enrolments)
            .Where(d => d.Status == DriveStatus.Open
                        || d.Status == DriveStatus.Confirming
                        || d.Status == DriveStatus.Confirmed
                        || d.ModifiedAtUtc > cutoff)
            .OrderByDescending(d => d.Status == DriveStatus.Open)
            .ThenBy(d => d.CutOffAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        return Results.Ok(drives.Select(ToView));
    }

    private static async Task<IResult> DetailAsync(
        Guid id,
        DrivesDbContext context,
        CancellationToken cancellationToken)
    {
        var drive = await context.Drives
            .AsNoTracking()
            .Include(d => d.Enrolments)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        return drive is null
            ? Error.NotFound("drive.not_found", "No such drive.").ToProblem()
            : Results.Ok(ToView(drive));
    }

    private static async Task<IResult> EnrolAsync(
        Guid id,
        EnrolRequest request,
        EnrolmentService enrolments,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var result = await enrolments.EnrolAsync(
            id, currentUser.RequireUserId(), request.FlatId, request.Units, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var enrolment = result.Value;

        return Results.Ok(new
        {
            enrolmentId = enrolment.Id,
            units = enrolment.Units,
            unitPricePaise = enrolment.UnitPriceAtJoinPaise,
            amountPaise = enrolment.AmountChargedPaise,

            // Stated at the point of joining, because it is the thing a resident is most
            // likely to be surprised by later: the price can fall further, and if it does they
            // get the difference back rather than having overpaid.
            note = "If more neighbours join, the price falls and you are refunded the difference.",
        });
    }

    private static async Task<IResult> WithdrawAsync(
        Guid id,
        Guid flatId,
        EnrolmentService enrolments,
        CancellationToken cancellationToken)
    {
        var result = await enrolments.WithdrawAsync(id, flatId, cancellationToken);

        return result.IsFailure ? result.ToProblem() : Results.NoContent();
    }

    private static DriveView ToView(ServiceDrive drive) => new(
        drive.Id,
        drive.ServiceCode,
        drive.Status.ToString(),
        drive.VendorId,
        drive.ActiveParticipantCount,
        drive.ActiveUnitCount,
        drive.Quorum,
        drive.Capacity,
        Math.Max(0, drive.Quorum - drive.ActiveParticipantCount),
        drive.HasReachedQuorum,
        drive.CutOffAtUtc,
        drive.ServiceDateUtc,
        drive.FinalUnitPricePaise);
}
