using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Gate.Api.Domain;
using SocietyHub.Gate.Api.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Gate.Api.Features;

public sealed record PunchRequest(string BadgeCode, EntryDirection Direction);

public sealed record RegisterHelpRequest(
    string FullName,
    string PhoneNumber,
    HelpCategory Category,
    string BadgeCode,
    IReadOnlyList<Guid> FlatIds);

public sealed record AttendanceDay(DateOnly WorkDate, bool Present, int? MinutesOnSite, int PunchCount);

public sealed record MonthlySheet(
    Guid DailyHelpId,
    string FullName,
    string Category,
    int Year,
    int Month,
    int DaysPresent,
    IReadOnlyList<AttendanceDay> Days);

public sealed class PunchValidator : AbstractValidator<PunchRequest>
{
    public PunchValidator() =>
        RuleFor(r => r.BadgeCode).NotEmpty().WithErrorCode("Badge.Required");
}

public sealed class RegisterHelpValidator : AbstractValidator<RegisterHelpRequest>
{
    public RegisterHelpValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().WithErrorCode("Name.Required");
        RuleFor(r => r.PhoneNumber).NotEmpty().WithErrorCode("Phone.Required");
        RuleFor(r => r.BadgeCode).NotEmpty().WithErrorCode("Badge.Required");
        RuleFor(r => r.FlatIds).NotEmpty().WithErrorCode("Flat.AtLeastOne");
    }
}

/// <summary>
/// Attendance for domestic workers.
///
/// A badge scan rather than a biometric, deliberately. This is the least powerful group the
/// platform touches and the one least able to refuse — a card they can hand back at the end
/// of an engagement is a fundamentally different relationship to a face template they cannot.
/// </summary>
public static class AttendanceEndpoints
{
    public static IEndpointRouteBuilder MapAttendanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/attendance").WithTags("Daily help attendance");

        group.MapPost("/help", RegisterHelpAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithValidation<RegisterHelpRequest>()
             .WithSummary("Registers a domestic worker and assigns them to flats.");

        group.MapPost("/punch", PunchAsync)
             .RequireAuthorization(SocietyHubPolicies.GateOperations)
             .WithValidation<PunchRequest>()
             .WithSummary("Records a badge scan at the gate.");

        group.MapGet("/sheet/{dailyHelpId:guid}", MonthlySheetAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Returns one worker's attendance for a month.");

        return app;
    }

    private static async Task<IResult> RegisterHelpAsync(
        RegisterHelpRequest request,
        GateDbContext context,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();

        var duplicate = await context.DailyHelps
            .AnyAsync(h => h.BadgeCode == request.BadgeCode, cancellationToken);

        if (duplicate)
        {
            return Result
                .Failure(Error.Conflict("Badge.InUse", "That badge is already assigned."))
                .ToProblem();
        }

        var help = new DailyHelp(
            Guid.CreateVersion7(),
            societyId,
            request.FullName,
            request.PhoneNumber,
            request.Category)
        {
            BadgeCode = request.BadgeCode,
        };

        // One worker, many flats. A maid working six flats is one person the gate recognises.
        foreach (var flatId in request.FlatIds.Distinct())
        {
            help.AssignToFlat(flatId);
        }

        context.DailyHelps.Add(help);
        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/attendance/help/{help.Id}",
            new { help.Id, help.FullName, Assignments = help.Assignments.Count });
    }

    private static async Task<IResult> PunchAsync(
        PunchRequest request,
        GateDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        ILocaleContext locale,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();
        var now = timeProvider.GetUtcNow();

        var help = await context.DailyHelps
            .SingleOrDefaultAsync(
                h => h.BadgeCode == request.BadgeCode && h.IsActive, cancellationToken);

        if (help is null)
        {
            return Result
                .Failure(Error.NotFound("Badge.Unknown", "That badge is not recognised."))
                .ToProblem();
        }

        // The society's local date, not UTC. A maid arriving at 05:30 IST is on the previous
        // UTC day, and using UTC would put her first shift of the month in the month before —
        // on the sheet she is paid from.
        var workDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(now, locale.TimeZone).DateTime);

        var record = await context.AttendanceRecords
            .SingleOrDefaultAsync(
                r => r.DailyHelpId == help.Id && r.WorkDate == workDate, cancellationToken);

        if (record is null)
        {
            record = new AttendanceRecord(Guid.CreateVersion7(), societyId, help.Id, workDate);
            context.AttendanceRecords.Add(record);
        }

        var result = request.Direction == EntryDirection.Inbound
            ? record.PunchIn(now)
            : record.PunchOut(now);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        // Also logged as gate movement, so "who is inside" includes staff during a fire.
        context.GateEntries.Add(
            new GateEntry(Guid.CreateVersion7(), societyId, request.Direction, now)
            {
                DailyHelpId = help.Id,
                PersonName = help.FullName,
                PersonPhone = help.PhoneNumber,
                VisitorType = VisitorType.Staff,
                RecordedByGuardId = currentUser.UserId,
            });

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(new
        {
            help.FullName,
            workDate,
            direction = request.Direction.ToString(),
            record.PunchCount,
        });
    }

    private static async Task<IResult> MonthlySheetAsync(
        Guid dailyHelpId,
        int? year,
        int? month,
        GateDbContext context,
        ILocaleContext locale,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), locale.TimeZone);
        var targetYear = year ?? localNow.Year;
        var targetMonth = month ?? localNow.Month;

        if (targetMonth is < 1 or > 12)
        {
            return Result
                .Failure(Error.Validation("Sheet.BadMonth", "Month must be between 1 and 12."))
                .ToProblem();
        }

        var help = await context.DailyHelps
            .SingleOrDefaultAsync(h => h.Id == dailyHelpId, cancellationToken);

        if (help is null)
        {
            return Result.Failure(Error.NotFound("Help.NotFound", "No such worker.")).ToProblem();
        }

        var from = new DateOnly(targetYear, targetMonth, 1);
        var to = from.AddMonths(1);

        var records = await context.AttendanceRecords
            .Where(r => r.DailyHelpId == dailyHelpId && r.WorkDate >= from && r.WorkDate < to)
            .OrderBy(r => r.WorkDate)
            .ToListAsync(cancellationToken);

        var days = records
            .Select(r => new AttendanceDay(r.WorkDate, r.IsPresent, r.MinutesOnSite, r.PunchCount))
            .ToList();

        return Microsoft.AspNetCore.Http.Results.Ok(new MonthlySheet(
            help.Id,
            help.FullName,
            help.Category.ToString(),
            targetYear,
            targetMonth,
            days.Count(d => d.Present),
            days));
    }
}
