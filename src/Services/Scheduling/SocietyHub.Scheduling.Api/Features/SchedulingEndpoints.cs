using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Features;
using SocietyHub.Scheduling.Api.Domain;
using SocietyHub.Scheduling.Api.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Features;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Scheduling.Api.Features;

public sealed record CreateSlotRequest(
    Guid DriveId, DateOnly ServiceDate, TimeOnly StartsAt, TimeOnly EndsAt);

public sealed record AssignTechnicianRequest(Guid TechnicianId, string Name, int Jobs);

public sealed record BookJobRequest(Guid SlotId, Guid EnrolmentId, Guid FlatId, int Units);

public sealed record CompleteJobRequest(string Code, string? ProofPhotoKey, string? Notes);

public sealed record RateJobRequest(int Rating, string? Comment);

public sealed record RescheduleRequest(Guid SlotId);

public sealed record ReasonRequest(string Reason);

/// <summary>
/// What a resident sees about their job. Includes the completion code, because it is theirs to
/// give — the technician never sees it here.
/// </summary>
public sealed record MyJobView(
    Guid Id,
    string Status,
    DateOnly ServiceDate,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    string? TechnicianName,
    string CompletionCode,
    int Units,
    int? MyRating);

/// <summary>
/// What a technician sees. Deliberately has no completion code — the whole point of the code
/// is that it comes from the resident at the door.
/// </summary>
public sealed record TechnicianJobView(
    Guid Id, string Status, Guid FlatId, int Units, TimeOnly StartsAt, TimeOnly EndsAt);

public sealed class CreateSlotValidator : AbstractValidator<CreateSlotRequest>
{
    public CreateSlotValidator() =>
        RuleFor(r => r)
            .Must(r => r.EndsAt > r.StartsAt)
            .WithErrorCode("Slot.EndsBeforeStart")
            .WithMessage("A slot must end after it starts.");
}

public sealed class CompleteJobValidator : AbstractValidator<CompleteJobRequest>
{
    public CompleteJobValidator() =>
        RuleFor(r => r.Code)
            .NotEmpty().WithErrorCode("Job.CodeRequired")
            .Length(4).WithErrorCode("Job.BadCodeLength");
}

public static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder app)
    {
        var slots = app.MapGroup("/api/slots").WithTags("Slots");

        slots.MapPost("/", CreateSlotAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithValidation<CreateSlotRequest>()
             .WithSummary("Creates a service window on a drive's service day.");

        slots.MapPost("/{id:guid}/technicians", AssignTechnicianAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithSummary("Puts a technician on a slot, which is what gives it capacity.");

        slots.MapGet("/", ListSlotsAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .RequireFeature(FeatureKey.BulkServiceDrives)
             .WithSummary("Slots for a drive, with the places left in each.");

        var jobs = app.MapGroup("/api/jobs").WithTags("Jobs");

        jobs.MapPost("/", BookAsync)
            .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
            .RequireFeature(FeatureKey.BulkServiceDrives)
            .WithSummary("Books a paid enrolment into a slot.");

        jobs.MapGet("/mine", MyJobsAsync)
            .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
            .RequireFeature(FeatureKey.BulkServiceDrives)
            .WithSummary("The caller's jobs, including the code to give the technician.");

        jobs.MapPost("/{id:guid}/en-route", EnRouteAsync)
            .RequireAuthorization(SocietyHubPolicies.RequireSociety)
            .WithSummary("Technician is on the way.");

        jobs.MapPost("/{id:guid}/start", StartAsync)
            .RequireAuthorization(SocietyHubPolicies.RequireSociety)
            .WithSummary("Technician has begun work.");

        jobs.MapPost("/{id:guid}/complete", CompleteAsync)
            .RequireAuthorization(SocietyHubPolicies.RequireSociety)
            .WithValidation<CompleteJobRequest>()
            .WithSummary("Completes a job against the resident's code.");

        jobs.MapPost("/{id:guid}/rate", RateAsync)
            .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
            .WithSummary("Rates a completed job, afterwards and privately.");

        jobs.MapPost("/{id:guid}/reschedule", RescheduleAsync)
            .RequireAuthorization(SocietyHubPolicies.RequireSociety)
            .WithSummary("Moves a job to another slot.");

        jobs.MapPost("/{id:guid}/cancel", CancelAsync)
            .RequireAuthorization(SocietyHubPolicies.RequireSociety)
            .WithSummary("Cancels a job that has not happened.");

        jobs.MapPost("/{id:guid}/resident-unavailable", UnavailableAsync)
            .RequireAuthorization(SocietyHubPolicies.RequireSociety)
            .WithSummary("Technician attended and could not get in.");

        return app;
    }

    private static async Task<IResult> CreateSlotAsync(
        CreateSlotRequest request,
        SchedulingDbContext context,
        ITenantContext tenant,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var slot = new ServiceSlot(
            Guid.CreateVersion7(),
            tenant.RequireSocietyId(),
            request.DriveId,
            request.ServiceDate,
            request.StartsAt,
            request.EndsAt,
            timeProvider.GetUtcNow());

        context.Slots.Add(slot);
        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/slots/{slot.Id}", new { id = slot.Id });
    }

    private static async Task<IResult> AssignTechnicianAsync(
        Guid id,
        AssignTechnicianRequest request,
        SchedulingDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var slot = await context.Slots
            .Include(s => s.Technicians)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (slot is null)
        {
            return Error.NotFound("slot.not_found", "No such slot.").ToProblem();
        }

        var result = slot.AssignTechnician(
            request.TechnicianId, request.Name, request.Jobs, timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { slot.Id, capacity = slot.Capacity, placesLeft = slot.PlacesLeft });
    }

    private static async Task<IResult> ListSlotsAsync(
        Guid driveId,
        SchedulingDbContext context,
        CancellationToken cancellationToken)
    {
        var slots = await context.Slots
            .AsNoTracking()
            .Include(s => s.Technicians)
            .Where(s => s.DriveId == driveId && !s.IsCancelled)
            .OrderBy(s => s.ServiceDate)
            .ThenBy(s => s.StartsAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(slots.Select(s => new
        {
            s.Id,
            s.ServiceDate,
            s.StartsAt,
            s.EndsAt,
            s.Capacity,
            s.BookedCount,
            s.PlacesLeft,
            Technicians = s.Technicians.Select(t => t.TechnicianName),
        }));
    }

    /// <summary>
    /// Books a job into a slot and takes the place in one transaction.
    ///
    /// The slot's booked count and the job are written together deliberately. Two writes would
    /// let a crash between them either lose the job or hold a place for one that does not
    /// exist, and the second is worse — it silently shrinks a slot nobody can refill.
    /// </summary>
    private static async Task<IResult> BookAsync(
        BookJobRequest request,
        SchedulingDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var slot = await context.Slots
            .Include(s => s.Technicians)
            .FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken);

        if (slot is null)
        {
            return Error.NotFound("slot.not_found", "No such slot.").ToProblem();
        }

        var existing = await context.Jobs
            .FirstOrDefaultAsync(j => j.EnrolmentId == request.EnrolmentId, cancellationToken);

        if (existing is not null)
        {
            // Idempotent rather than an error. A resident tapping twice on a slow connection
            // should get their existing booking back, not a duplicate or a failure.
            return Results.Ok(new { id = existing.Id, slotId = existing.SlotId });
        }

        var booked = slot.Book(1, timeProvider.GetUtcNow());

        if (booked.IsFailure)
        {
            return booked.ToProblem();
        }

        var job = new ServiceJob(
            Guid.CreateVersion7(),
            tenant.RequireSocietyId(),
            slot.DriveId,
            request.EnrolmentId,
            slot.Id,
            currentUser.RequireUserId(),
            request.FlatId,
            request.Units,
            timeProvider.GetUtcNow());

        context.Jobs.Add(job);
        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/jobs/{job.Id}", new { id = job.Id });
    }

    private static async Task<IResult> MyJobsAsync(
        SchedulingDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();

        var jobs = await context.Jobs
            .AsNoTracking()
            .Where(j => j.ResidentUserId == userId)
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        var slotIds = jobs.Select(j => j.SlotId).Distinct().ToList();

        var slots = await context.Slots
            .AsNoTracking()
            .Where(s => slotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        return Results.Ok(jobs
            .Where(j => slots.ContainsKey(j.SlotId))
            .Select(j =>
            {
                var slot = slots[j.SlotId];

                return new MyJobView(
                    j.Id,
                    j.Status.ToString(),
                    slot.ServiceDate,
                    slot.StartsAt,
                    slot.EndsAt,
                    j.TechnicianName,
                    j.CompletionCode,
                    j.Units,
                    j.ResidentRating);
            }));
    }

    private static Task<IResult> EnRouteAsync(
        Guid id, SchedulingDbContext context, TimeProvider time, CancellationToken ct) =>
        MutateAsync(id, context, ct, job => job.MarkEnRoute(time.GetUtcNow()));

    private static Task<IResult> StartAsync(
        Guid id, SchedulingDbContext context, TimeProvider time, CancellationToken ct) =>
        MutateAsync(id, context, ct, job => job.Start(time.GetUtcNow()));

    private static Task<IResult> CompleteAsync(
        Guid id,
        CompleteJobRequest request,
        SchedulingDbContext context,
        TimeProvider time,
        CancellationToken ct) =>
        MutateAsync(id, context, ct, job =>
            job.CompleteWithCode(
                request.Code, request.ProofPhotoKey, request.Notes, time.GetUtcNow()));

    private static Task<IResult> RateAsync(
        Guid id,
        RateJobRequest request,
        SchedulingDbContext context,
        TimeProvider time,
        CancellationToken ct) =>
        MutateAsync(id, context, ct, job =>
            job.Rate(request.Rating, request.Comment, time.GetUtcNow()));

    private static Task<IResult> CancelAsync(
        Guid id,
        ReasonRequest request,
        SchedulingDbContext context,
        TimeProvider time,
        CancellationToken ct) =>
        MutateAsync(id, context, ct, job => job.Cancel(request.Reason, time.GetUtcNow()));

    private static Task<IResult> UnavailableAsync(
        Guid id,
        ReasonRequest request,
        SchedulingDbContext context,
        TimeProvider time,
        CancellationToken ct) =>
        MutateAsync(id, context, ct, job =>
            job.MarkResidentUnavailable(request.Reason, time.GetUtcNow()));

    /// <summary>
    /// Moves a job between slots, releasing the old place and taking a new one.
    ///
    /// Both slot counts and the job move together. Releasing without taking would leave a
    /// resident with no booking; taking without releasing shrinks a slot permanently.
    /// </summary>
    private static async Task<IResult> RescheduleAsync(
        Guid id,
        RescheduleRequest request,
        SchedulingDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var job = await context.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job is null)
        {
            return Error.NotFound("job.not_found", "No such job.").ToProblem();
        }

        var target = await context.Slots
            .Include(s => s.Technicians)
            .FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken);

        if (target is null)
        {
            return Error.NotFound("slot.not_found", "No such slot.").ToProblem();
        }

        var source = await context.Slots
            .Include(s => s.Technicians)
            .FirstOrDefaultAsync(s => s.Id == job.SlotId, cancellationToken);

        var now = timeProvider.GetUtcNow();

        // Take the new place before releasing the old one. If the target is full, the resident
        // keeps the slot they had rather than ending up with none.
        var booked = target.Book(1, now);

        if (booked.IsFailure)
        {
            return booked.ToProblem();
        }

        var moved = job.RescheduleTo(target.Id, now);

        if (moved.IsFailure)
        {
            target.Release(1, now);
            return moved.ToProblem();
        }

        source?.Release(1, now);

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { job.Id, job.SlotId, job.RescheduleCount });
    }

    private static async Task<IResult> MutateAsync(
        Guid id,
        SchedulingDbContext context,
        CancellationToken cancellationToken,
        Func<ServiceJob, Result> operation)
    {
        var job = await context.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);

        if (job is null)
        {
            return Error.NotFound("job.not_found", "No such job.").ToProblem();
        }

        var result = operation(job);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { job.Id, status = job.Status.ToString() });
    }
}
