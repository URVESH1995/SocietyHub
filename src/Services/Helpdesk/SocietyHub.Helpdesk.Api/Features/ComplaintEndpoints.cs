using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Helpdesk;
using SocietyHub.Helpdesk.Api.Domain;
using SocietyHub.Helpdesk.Api.Persistence;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Helpdesk.Api.Features;

public sealed record RaiseComplaintRequest(
    Guid FlatId,
    ComplaintCategory Category,
    ComplaintPriority Priority,
    string Title,
    string Description);

public sealed record AssignRequest(Guid AssigneeId, string AssigneeName);

public sealed record ResolveRequest(string Resolution);

public sealed record CloseRequest(int? Rating, string? Comment);

public sealed record ReopenRequest(string Reason);

public sealed record AddNoteRequest(string Body, bool InternalOnly);

public sealed record ComplaintSummary(
    Guid Id,
    string TicketNumber,
    string Category,
    string Priority,
    string Status,
    string Title,
    DateTimeOffset RaisedAtUtc,
    DateTimeOffset SlaDueAtUtc,
    bool IsBreached,
    string? AssignedToName,
    int EscalationLevel);

public sealed class RaiseComplaintValidator : AbstractValidator<RaiseComplaintRequest>
{
    public RaiseComplaintValidator()
    {
        RuleFor(r => r.FlatId).NotEmpty().WithErrorCode("Flat.Required");

        RuleFor(r => r.Title)
            .NotEmpty().WithErrorCode("Complaint.TitleRequired")
            .MaximumLength(200).WithErrorCode("Complaint.TitleTooLong");

        RuleFor(r => r.Description)
            .NotEmpty().WithErrorCode("Complaint.DescriptionRequired")
            .MaximumLength(4000).WithErrorCode("Complaint.DescriptionTooLong");
    }
}

public sealed class ResolveValidator : AbstractValidator<ResolveRequest>
{
    public ResolveValidator() =>
        // "Fixed" gives the resident nothing to verify against and the next person nothing to
        // learn from, so a real sentence is required.
        RuleFor(r => r.Resolution)
            .NotEmpty().WithErrorCode("Complaint.ResolutionRequired")
            .MinimumLength(10).WithErrorCode("Complaint.ResolutionTooShort");
}

public sealed class CloseValidator : AbstractValidator<CloseRequest>
{
    public CloseValidator() =>
        RuleFor(r => r.Rating)
            .InclusiveBetween(1, 5).When(r => r.Rating is not null)
            .WithErrorCode("Complaint.BadRating");
}

public sealed class AddNoteValidator : AbstractValidator<AddNoteRequest>
{
    public AddNoteValidator() =>
        RuleFor(r => r.Body).NotEmpty().WithErrorCode("Note.BodyRequired");
}

public static class ComplaintEndpoints
{
    public static IEndpointRouteBuilder MapComplaintEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/complaints").WithTags("Complaints");

        group.MapPost("/", RaiseAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithValidation<RaiseComplaintRequest>()
             .WithSummary("Raises a complaint and starts the SLA clock.");

        group.MapGet("/mine", MineAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithSummary("Lists the caller's complaints.");

        group.MapGet("/overdue", OverdueAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .WithSummary("Lists complaints that have breached their SLA.");

        group.MapPost("/{id:guid}/assign", AssignAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithSummary("Assigns a complaint to a person.");

        group.MapPost("/{id:guid}/start", StartAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithSummary("Marks work as started.");

        group.MapPost("/{id:guid}/resolve", ResolveAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithValidation<ResolveRequest>()
             .WithSummary("Records what was done to fix it.");

        group.MapPost("/{id:guid}/close", CloseAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithValidation<CloseRequest>()
             .WithSummary("Resident confirms the fix and optionally rates it.");

        group.MapPost("/{id:guid}/reopen", ReopenAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithSummary("Resident says it is not actually fixed. Does not reset the deadline.");

        group.MapPost("/{id:guid}/notes", AddNoteAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithValidation<AddNoteRequest>()
             .WithSummary("Adds a note, optionally internal-only.");

        return app;
    }

    private static async Task<IResult> RaiseAsync(
        RaiseComplaintRequest request,
        HelpdeskDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        ICurrentUser currentUser,
        ILocaleContext locale,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();
        var now = timeProvider.GetUtcNow();

        // The resident picked a category from a dropdown while stressed; some categories are
        // urgent regardless of what they selected.
        var priority = SlaPolicy.EffectivePriority(request.Category, request.Priority);

        // Working hours, in the society's own timezone. A complaint raised at 11pm must not
        // burn its budget overnight while nobody could act on it.
        var dueAt = SlaPolicy.CalculateDueAt(now, priority, locale.TimeZone);

        var localYear = TimeZoneInfo.ConvertTime(now, locale.TimeZone).Year;
        var ticketNumber = await context.NextTicketNumberAsync(societyId, localYear, cancellationToken);

        var complaint = new Complaint(
            Guid.CreateVersion7(),
            societyId,
            request.FlatId,
            currentUser.RequireUserId(),
            ticketNumber,
            request.Category,
            priority,
            request.Title,
            request.Description,
            now,
            dueAt);

        context.Complaints.Add(complaint);

        outbox.Enqueue(new ComplaintRaised
        {
            SocietyId = societyId,
            ComplaintId = complaint.Id,
            TicketNumber = complaint.TicketNumber,
            FlatId = complaint.FlatId,
            RaisedByUserId = complaint.RaisedByUserId,
            Category = complaint.Category.ToString(),
            Title = complaint.Title,
            Priority = complaint.Priority.ToString(),
            SlaDueAtUtc = complaint.SlaDueAtUtc,
        });

        // The ticket number and the counter increment commit together, so a failed insert
        // cannot consume a number and leave a gap in the sequence.
        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/complaints/{complaint.Id}",
            new { complaint.Id, complaint.TicketNumber, complaint.SlaDueAtUtc, priority = priority.ToString() });
    }

    private static async Task<IResult> MineAsync(
        HelpdeskDbContext context,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var now = timeProvider.GetUtcNow();

        var mine = await context.Complaints
            .Where(c => c.RaisedByUserId == userId)
            .OrderByDescending(c => c.RaisedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(mine.Select(c => ToSummary(c, now)));
    }

    private static async Task<IResult> OverdueAsync(
        HelpdeskDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var overdue = await context.Complaints
            .Where(c => c.Status != ComplaintStatus.Closed
                        && c.Status != ComplaintStatus.Rejected
                        && c.ResolvedAtUtc == null
                        && c.SlaDueAtUtc < now)
            .OrderBy(c => c.SlaDueAtUtc)
            .ToListAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Ok(overdue.Select(c => ToSummary(c, now)));
    }

    private static async Task<IResult> AssignAsync(
        Guid id,
        AssignRequest request,
        HelpdeskDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var complaint = await Find(context, id, cancellationToken);

        if (complaint is null)
        {
            return NotFound();
        }

        var result = complaint.Assign(request.AssigneeId, request.AssigneeName, timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        outbox.Enqueue(new ComplaintAssigned
        {
            SocietyId = tenant.RequireSocietyId(),
            ComplaintId = complaint.Id,
            TicketNumber = complaint.TicketNumber,
            AssigneeId = request.AssigneeId,
            AssigneeName = request.AssigneeName,
        });

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> StartAsync(
        Guid id,
        HelpdeskDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var complaint = await Find(context, id, cancellationToken);

        if (complaint is null)
        {
            return NotFound();
        }

        var result = complaint.Start(timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> ResolveAsync(
        Guid id,
        ResolveRequest request,
        HelpdeskDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var complaint = await Find(context, id, cancellationToken);

        if (complaint is null)
        {
            return NotFound();
        }

        var result = complaint.Resolve(request.Resolution, now);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        outbox.Enqueue(new ComplaintResolved
        {
            SocietyId = tenant.RequireSocietyId(),
            ComplaintId = complaint.Id,
            TicketNumber = complaint.TicketNumber,
            RaisedByUserId = complaint.RaisedByUserId,
            ResolvedAtUtc = now,
            // Judged against resolution, not closure — closure waits on a resident who may be
            // travelling, and the metric must measure the society, not them.
            WithinSla = !complaint.HasBreachedSla(now),
        });

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> CloseAsync(
        Guid id,
        CloseRequest request,
        HelpdeskDbContext context,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var complaint = await Find(context, id, cancellationToken);

        if (complaint is null)
        {
            return NotFound();
        }

        // Only the person who raised it may close it. A society able to close its own tickets
        // would report perfect compliance and fix nothing.
        if (complaint.RaisedByUserId != currentUser.RequireUserId())
        {
            return Result
                .Failure(Error.Forbidden(
                    "Complaint.NotYours", "Only the resident who raised it can close it."))
                .ToProblem();
        }

        var result = complaint.Close(request.Rating, request.Comment, timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> ReopenAsync(
        Guid id,
        ReopenRequest request,
        HelpdeskDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var complaint = await Find(context, id, cancellationToken);

        if (complaint is null)
        {
            return NotFound();
        }

        var result = complaint.Reopen(request.Reason, timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> AddNoteAsync(
        Guid id,
        AddNoteRequest request,
        HelpdeskDbContext context,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var complaint = await Find(context, id, cancellationToken);

        if (complaint is null)
        {
            return NotFound();
        }

        complaint.AddNote(
            currentUser.RequireUserId(),
            request.Body,
            timeProvider.GetUtcNow(),
            request.InternalOnly);

        await context.SaveChangesAsync(cancellationToken);
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static Task<Complaint?> Find(
        HelpdeskDbContext context,
        Guid id,
        CancellationToken cancellationToken) =>
        context.Complaints
            .Include(c => c.Notes)
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    private static IResult NotFound() =>
        Result.Failure(Error.NotFound("Complaint.NotFound", "No such complaint.")).ToProblem();

    private static ComplaintSummary ToSummary(Complaint c, DateTimeOffset now) =>
        new(c.Id,
            c.TicketNumber,
            c.Category.ToString(),
            c.Priority.ToString(),
            c.Status.ToString(),
            c.Title,
            c.RaisedAtUtc,
            c.SlaDueAtUtc,
            c.HasBreachedSla(now),
            c.AssignedToName,
            c.EscalationLevel);
}
