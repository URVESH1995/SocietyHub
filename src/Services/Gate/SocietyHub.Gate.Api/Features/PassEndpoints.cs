using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Gate;
using SocietyHub.Gate.Api.Domain;
using SocietyHub.Gate.Api.Persistence;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Gate.Api.Features;

public sealed record PreApproveRequest(
    Guid FlatId,
    string VisitorName,
    string? VisitorPhone,
    VisitorType VisitorType,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidUntilUtc,
    int ExpectedPersonCount,
    string? Purpose);

public sealed record PassIssuedResponse(Guid PassId, string Code, DateTimeOffset ValidUntilUtc);

public sealed record CheckInRequest(string Code, string? VehicleNumber, string? PhotoBlobKey);

public sealed record WalkUpEntryRequest(
    Guid FlatId,
    string VisitorName,
    string? VisitorPhone,
    VisitorType VisitorType,
    string? VehicleNumber,
    string? PhotoBlobKey,
    bool LeftAtGate,
    string? Notes);

public sealed class PreApproveValidator : AbstractValidator<PreApproveRequest>
{
    public PreApproveValidator()
    {
        RuleFor(r => r.FlatId).NotEmpty().WithErrorCode("Flat.Required");

        RuleFor(r => r.VisitorName)
            .NotEmpty().WithErrorCode("Visitor.NameRequired")
            .MaximumLength(200).WithErrorCode("Visitor.NameTooLong");

        RuleFor(r => r.ValidUntilUtc)
            .GreaterThan(r => r.ValidFromUtc).WithErrorCode("Pass.WindowInverted");

        // A pass valid for a week is a standing key to the building. Long visits are handled
        // by re-issuing, which keeps each admission a deliberate act.
        RuleFor(r => r)
            .Must(r => r.ValidUntilUtc - r.ValidFromUtc <= TimeSpan.FromHours(24))
            .WithErrorCode("Pass.WindowTooLong")
            .WithMessage("A pass may not be valid for more than 24 hours.");

        RuleFor(r => r.ExpectedPersonCount)
            .InclusiveBetween(1, 20).WithErrorCode("Pass.PersonCountOutOfRange");
    }
}

public sealed class WalkUpEntryValidator : AbstractValidator<WalkUpEntryRequest>
{
    public WalkUpEntryValidator()
    {
        RuleFor(r => r.FlatId).NotEmpty().WithErrorCode("Flat.Required");
        RuleFor(r => r.VisitorName).NotEmpty().WithErrorCode("Visitor.NameRequired");
    }
}

public static class PassEndpoints
{
    public static IEndpointRouteBuilder MapPassEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/passes").WithTags("Visit passes");

        group.MapPost("/", PreApproveAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithValidation<PreApproveRequest>()
             .WithSummary("Pre-approves a visitor and returns a one-time gate code.");

        group.MapPost("/{passId:guid}/check-in", CheckInAsync)
             .RequireAuthorization(SocietyHubPolicies.GateOperations)
             .WithValidation<CheckInRequest>()
             .WithSummary("Admits a visitor after verifying their code.");

        group.MapPost("/{passId:guid}/check-out", CheckOutAsync)
             .RequireAuthorization(SocietyHubPolicies.GateOperations)
             .WithSummary("Records a visitor leaving.");

        group.MapPost("/{passId:guid}/cancel", CancelAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithSummary("Cancels an unused pass.");

        group.MapPost("/walk-up", WalkUpAsync)
             .RequireAuthorization(SocietyHubPolicies.GateOperations)
             .WithValidation<WalkUpEntryRequest>()
             .WithSummary("Logs a delivery or cab that arrived without a pass.");

        group.MapGet("/expected", ExpectedTodayAsync)
             .RequireAuthorization(SocietyHubPolicies.GateOperations)
             .WithSummary("Lists passes currently open at this society.");

        return app;
    }

    private static async Task<IResult> PreApproveAsync(
        PreApproveRequest request,
        GateDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();

        // Blacklist is advisory at the gate but blocking here: refusing to *issue* a pass is a
        // decision the resident can see and argue with, whereas silently failing them at the
        // gate would strand a visitor with no explanation.
        if (!string.IsNullOrWhiteSpace(request.VisitorPhone))
        {
            var flagged = await context.BlacklistEntries
                .AnyAsync(
                    b => b.PhoneNumber == request.VisitorPhone && b.IsActive,
                    cancellationToken);

            if (flagged)
            {
                return Result
                    .Failure(Error.Conflict(
                        "Visitor.Blacklisted",
                        "That number is flagged by the committee. Contact them to proceed."))
                    .ToProblem();
            }
        }

        var (pass, code) = VisitPass.Issue(
            societyId,
            request.FlatId,
            currentUser.RequireUserId(),
            request.VisitorName,
            request.VisitorPhone,
            request.VisitorType,
            request.ValidFromUtc,
            request.ValidUntilUtc,
            request.ExpectedPersonCount);

        pass.Purpose = request.Purpose;
        context.VisitPasses.Add(pass);

        outbox.Enqueue(new VisitorPreApproved
        {
            SocietyId = societyId,
            VisitPassId = pass.Id,
            FlatId = pass.FlatId,
            VisitorName = pass.VisitorName,
            VisitorPhone = pass.VisitorPhone ?? string.Empty,
            VisitorType = pass.VisitorType.ToString(),
            ValidFromUtc = pass.ValidFromUtc,
            ValidUntilUtc = pass.ValidUntilUtc,
        });

        await context.SaveChangesAsync(cancellationToken);

        // The only moment the code exists in readable form. It is never returned again — a
        // resident who loses it re-issues rather than retrieving it.
        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/passes/{pass.Id}",
            new PassIssuedResponse(pass.Id, code, pass.ValidUntilUtc));
    }

    private static async Task<IResult> CheckInAsync(
        Guid passId,
        CheckInRequest request,
        GateDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var pass = await context.VisitPasses
            .SingleOrDefaultAsync(p => p.Id == passId, cancellationToken);

        if (pass is null)
        {
            return Result.Failure(Error.NotFound("Pass.NotFound", "No such pass.")).ToProblem();
        }

        var guardId = currentUser.RequireUserId();
        var result = pass.CheckIn(request.Code, guardId, now);

        pass.VehicleNumber = request.VehicleNumber ?? pass.VehicleNumber;
        pass.PhotoBlobKey = request.PhotoBlobKey ?? pass.PhotoBlobKey;

        // Saved even on failure, so the attempt counts against the cap.
        if (result.IsFailure)
        {
            await context.SaveChangesAsync(cancellationToken);
            return result.ToProblem();
        }

        context.GateEntries.Add(
            GateEntry.ForPass(pass, EntryDirection.Inbound, guardId, now));

        outbox.Enqueue(new VisitorCheckedIn
        {
            SocietyId = tenant.RequireSocietyId(),
            VisitPassId = pass.Id,
            FlatId = pass.FlatId,
            VisitorName = pass.VisitorName,
            VisitorType = pass.VisitorType.ToString(),
            CheckedInAtUtc = now,
            CheckedInByGuardId = guardId,
            VehicleNumber = pass.VehicleNumber,
        });

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(new { pass.Id, status = pass.Status.ToString() });
    }

    private static async Task<IResult> CheckOutAsync(
        Guid passId,
        GateDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var pass = await context.VisitPasses
            .SingleOrDefaultAsync(p => p.Id == passId, cancellationToken);

        if (pass is null)
        {
            return Result.Failure(Error.NotFound("Pass.NotFound", "No such pass.")).ToProblem();
        }

        var result = pass.CheckOut(now);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        context.GateEntries.Add(
            GateEntry.ForPass(pass, EntryDirection.Outbound, currentUser.RequireUserId(), now));

        outbox.Enqueue(new VisitorCheckedOut
        {
            SocietyId = tenant.RequireSocietyId(),
            VisitPassId = pass.Id,
            FlatId = pass.FlatId,
            CheckedOutAtUtc = now,
        });

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> CancelAsync(
        Guid passId,
        GateDbContext context,
        CancellationToken cancellationToken)
    {
        var pass = await context.VisitPasses
            .SingleOrDefaultAsync(p => p.Id == passId, cancellationToken);

        if (pass is null)
        {
            return Result.Failure(Error.NotFound("Pass.NotFound", "No such pass.")).ToProblem();
        }

        var result = pass.Cancel();

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    /// <summary>
    /// A delivery or cab that turned up unannounced, which is most of them. The guard records
    /// it and the resident is told; there was never a pass to verify.
    /// </summary>
    private static async Task<IResult> WalkUpAsync(
        WalkUpEntryRequest request,
        GateDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();
        var now = timeProvider.GetUtcNow();
        var guardId = currentUser.RequireUserId();

        var entry = new GateEntry(Guid.CreateVersion7(), societyId, EntryDirection.Inbound, now)
        {
            FlatId = request.FlatId,
            PersonName = request.VisitorName,
            PersonPhone = request.VisitorPhone,
            VisitorType = request.VisitorType,
            VehicleNumber = request.VehicleNumber,
            PhotoBlobKey = request.PhotoBlobKey,
            RecordedByGuardId = guardId,
            LeftAtGate = request.LeftAtGate,
            Notes = request.Notes,
        };

        context.GateEntries.Add(entry);

        outbox.Enqueue(new VisitorCheckedIn
        {
            SocietyId = societyId,
            // No pass exists, so the entry stands in as the correlating identifier.
            VisitPassId = entry.Id,
            FlatId = request.FlatId,
            VisitorName = request.VisitorName,
            VisitorType = request.VisitorType.ToString(),
            CheckedInAtUtc = now,
            CheckedInByGuardId = guardId,
            VehicleNumber = request.VehicleNumber,
        });

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/gate/entries/{entry.Id}", new { entry.Id, entry.LeftAtGate });
    }

    private static async Task<IResult> ExpectedTodayAsync(
        GateDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var open = await context.VisitPasses
            .Where(p => p.Status == PassStatus.Pending
                        && p.ValidFromUtc <= now
                        && p.ValidUntilUtc >= now)
            .OrderBy(p => p.ValidFromUtc)
            .Select(p => new
            {
                p.Id,
                p.VisitorName,
                p.VisitorPhone,
                VisitorType = p.VisitorType.ToString(),
                p.FlatId,
                p.ExpectedPersonCount,
                p.ValidUntilUtc,
            })
            .Take(500)
            .ToListAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(open);
    }
}
