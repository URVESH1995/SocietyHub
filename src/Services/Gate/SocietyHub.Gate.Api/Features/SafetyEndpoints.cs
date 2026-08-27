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

public sealed record RaiseSosRequest(
    SosCategory Category,
    Guid? FlatId,
    double? Latitude,
    double? Longitude,
    string? Description);

public sealed record ResolveSosRequest(string Notes, bool WasFalseAlarm);

public sealed record AddBlacklistRequest(
    string PhoneNumber,
    string? PersonName,
    string Reason,
    int ReviewInMonths);

public sealed class RaiseSosValidator : AbstractValidator<RaiseSosRequest>
{
    public RaiseSosValidator() =>
        // Nothing else is required. An SOS form that refuses to submit because a field is
        // missing is a form that failed at the only moment it mattered.
        RuleFor(r => r.Description).MaximumLength(2000).WithErrorCode("Sos.DescriptionTooLong");
}

public sealed class AddBlacklistValidator : AbstractValidator<AddBlacklistRequest>
{
    public AddBlacklistValidator()
    {
        RuleFor(r => r.PhoneNumber).NotEmpty().WithErrorCode("Phone.Required");

        // Mandatory, and long enough to be a sentence. An entry with no stated reason cannot
        // be reviewed or appealed, and becomes a permanent accusation.
        RuleFor(r => r.Reason)
            .NotEmpty().WithErrorCode("Blacklist.ReasonRequired")
            .MinimumLength(10).WithErrorCode("Blacklist.ReasonTooShort");

        RuleFor(r => r.ReviewInMonths)
            .InclusiveBetween(1, 24).WithErrorCode("Blacklist.ReviewOutOfRange");
    }
}

public static class SafetyEndpoints
{
    public static IEndpointRouteBuilder MapSafetyEndpoints(this IEndpointRouteBuilder app)
    {
        var sos = app.MapGroup("/api/sos").WithTags("SOS");

        sos.MapPost("/", RaiseAsync)
           .RequireAuthorization(SocietyHubPolicies.RequireSociety)
           .WithValidation<RaiseSosRequest>()
           .WithSummary("Raises a panic alert. Rides the Critical message lane.");

        sos.MapPost("/{incidentId:guid}/acknowledge", AcknowledgeAsync)
           .RequireAuthorization(SocietyHubPolicies.RequireSociety)
           .WithSummary("Marks an alert as picked up by a human.");

        sos.MapPost("/{incidentId:guid}/resolve", ResolveAsync)
           .RequireAuthorization(SocietyHubPolicies.RequireSociety)
           .WithValidation<ResolveSosRequest>()
           .WithSummary("Closes an alert.");

        sos.MapGet("/open", OpenAlertsAsync)
           .RequireAuthorization(SocietyHubPolicies.RequireSociety)
           .WithSummary("Lists unresolved alerts for the guard console.");

        var blacklist = app.MapGroup("/api/blacklist").WithTags("Blacklist");

        blacklist.MapPost("/", AddBlacklistAsync)
                 .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
                 .WithValidation<AddBlacklistRequest>()
                 .WithSummary("Flags a phone number. Committee decision, always attributed.");

        blacklist.MapPost("/{entryId:guid}/lift", LiftBlacklistAsync)
                 .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
                 .WithSummary("Lifts a flag.");

        blacklist.MapGet("/due-review", DueForReviewAsync)
                 .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
                 .WithSummary("Lists flags past their review date.");

        return app;
    }

    private static async Task<IResult> RaiseAsync(
        RaiseSosRequest request,
        GateDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();
        var now = timeProvider.GetUtcNow();

        var incident = new SosIncident(
            Guid.CreateVersion7(),
            societyId,
            currentUser.RequireUserId(),
            request.FlatId,
            request.Category,
            now)
        {
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Description = request.Description,
        };

        context.SosIncidents.Add(incident);

        // Through the outbox like everything else — the alert must not be published for a
        // transaction that then rolls back. The Critical lane is what makes the extra hop
        // acceptable: it is a dedicated queue with its own consumers and nothing queued ahead.
        outbox.Enqueue(new SosRaised
        {
            SocietyId = societyId,
            IncidentId = incident.Id,
            FlatId = request.FlatId ?? Guid.Empty,
            RaisedByUserId = incident.RaisedByUserId,
            Category = request.Category.ToString(),
            Latitude = request.Latitude,
            Longitude = request.Longitude,
        });

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/sos/{incident.Id}",
            new { incident.Id, raisedAtUtc = now, status = incident.Status.ToString() });
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid incidentId,
        GateDbContext context,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var incident = await context.SosIncidents
            .SingleOrDefaultAsync(s => s.Id == incidentId, cancellationToken);

        if (incident is null)
        {
            return Result.Failure(Error.NotFound("Sos.NotFound", "No such alert.")).ToProblem();
        }

        var result = incident.Acknowledge(currentUser.RequireUserId(), timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(new
        {
            incident.Id,
            // The number that says whether the alert actually worked.
            timeToAcknowledgeSeconds = incident.TimeToAcknowledge?.TotalSeconds,
        });
    }

    private static async Task<IResult> ResolveAsync(
        Guid incidentId,
        ResolveSosRequest request,
        GateDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var incident = await context.SosIncidents
            .SingleOrDefaultAsync(s => s.Id == incidentId, cancellationToken);

        if (incident is null)
        {
            return Result.Failure(Error.NotFound("Sos.NotFound", "No such alert.")).ToProblem();
        }

        // A false alarm is closed, never deleted. Someone who triggers one by accident and
        // finds no trace has no reason to believe a real one would have been recorded either.
        var result = incident.Resolve(request.Notes, request.WasFalseAlarm, timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> OpenAlertsAsync(
        GateDbContext context,
        CancellationToken cancellationToken)
    {
        var open = await context.SosIncidents
            .Where(s => s.Status == SosStatus.Raised || s.Status == SosStatus.Acknowledged)
            .OrderBy(s => s.RaisedAtUtc)
            .Select(s => new
            {
                s.Id,
                Category = s.Category.ToString(),
                Status = s.Status.ToString(),
                s.RaisedAtUtc,
                s.FlatId,
                s.Latitude,
                s.Longitude,
                s.Description,
            })
            .ToListAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(open);
    }

    private static async Task<IResult> AddBlacklistAsync(
        AddBlacklistRequest request,
        GateDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var entry = new BlacklistEntry(
            Guid.CreateVersion7(),
            tenant.RequireSocietyId(),
            request.PhoneNumber,
            request.Reason,
            currentUser.RequireUserId(),
            now.AddMonths(request.ReviewInMonths))
        {
            PersonName = request.PersonName,
        };

        context.BlacklistEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/blacklist/{entry.Id}", new { entry.Id, entry.ReviewDueAtUtc });
    }

    private static async Task<IResult> LiftBlacklistAsync(
        Guid entryId,
        string reason,
        GateDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entry = await context.BlacklistEntries
            .SingleOrDefaultAsync(b => b.Id == entryId, cancellationToken);

        if (entry is null)
        {
            return Result.Failure(Error.NotFound("Blacklist.NotFound", "No such entry.")).ToProblem();
        }

        entry.Lift(reason, timeProvider.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> DueForReviewAsync(
        GateDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var due = await context.BlacklistEntries
            .Where(b => b.IsActive && b.ReviewDueAtUtc <= now)
            .OrderBy(b => b.ReviewDueAtUtc)
            .Select(b => new { b.Id, b.PhoneNumber, b.PersonName, b.Reason, b.ReviewDueAtUtc })
            .ToListAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(due);
    }
}
