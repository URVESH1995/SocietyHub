using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Notice;
using SocietyHub.Notice.Api.Domain;
using SocietyHub.Notice.Api.Persistence;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Notice.Api.Features;

public sealed record PollOptionRequest(string LabelEn, string? LabelHi);

public sealed record CreatePollRequest(
    PollKind Kind,
    string QuestionEn,
    string? QuestionHi,
    Guid? NoticeId,
    IReadOnlyList<PollOptionRequest> Options);

public sealed record OpenPollRequest(
    DateTimeOffset ClosesAtUtc,
    int EligibleFlatCount,
    int QuorumPercent);

public sealed record CastVoteRequest(Guid FlatId, Guid OptionId);

public sealed class CreatePollValidator : AbstractValidator<CreatePollRequest>
{
    public CreatePollValidator()
    {
        RuleFor(r => r.QuestionEn)
            .NotEmpty().WithErrorCode("Poll.QuestionRequired")
            .MaximumLength(500).WithErrorCode("Poll.QuestionTooLong");

        RuleFor(r => r.Options)
            .NotNull().WithErrorCode("Poll.OptionsRequired")
            .Must(o => o is { Count: >= 2 and <= 10 })
            .WithErrorCode("Poll.OptionCount")
            .WithMessage("A poll needs between two and ten options.");
    }
}

public sealed class OpenPollValidator : AbstractValidator<OpenPollRequest>
{
    public OpenPollValidator()
    {
        RuleFor(r => r.QuorumPercent)
            .InclusiveBetween(0, 100).WithErrorCode("Poll.BadQuorum");

        RuleFor(r => r.EligibleFlatCount)
            .GreaterThan(0).WithErrorCode("Poll.NoEligibleFlats");
    }
}

public static class PollEndpoints
{
    public static IEndpointRouteBuilder MapPollEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/polls").WithTags("Polls");

        group.MapPost("/", CreateAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .WithValidation<CreatePollRequest>()
             .WithSummary("Drafts a poll with its options.");

        group.MapPost("/{id:guid}/open", OpenAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .WithValidation<OpenPollRequest>()
             .WithSummary("Opens voting and freezes the eligible-flat denominator.");

        group.MapPost("/{id:guid}/vote", VoteAsync)
             .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
             .WithSummary("Casts or changes this flat's vote while the poll is open.");

        group.MapPost("/{id:guid}/close", CloseAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .WithSummary("Closes voting and publishes the result.");

        group.MapGet("/", ListAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Lists open and recently closed polls.");

        group.MapGet("/{id:guid}/result", ResultAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("The tally, with counts hidden while a resolution is still open.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreatePollRequest request,
        NoticeDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var poll = new Poll(
            Guid.CreateVersion7(),
            tenant.RequireSocietyId(),
            currentUser.RequireUserId(),
            request.Kind,
            request.QuestionEn,
            timeProvider.GetUtcNow());

        if (request.QuestionHi is not null)
        {
            poll.SetHindi(request.QuestionHi);
        }

        if (request.NoticeId is not null)
        {
            poll.LinkToNotice(request.NoticeId.Value);
        }

        foreach (var option in request.Options)
        {
            var added = poll.AddOption(option.LabelEn, option.LabelHi);

            if (added.IsFailure)
            {
                return added.ToProblem();
            }
        }

        context.Polls.Add(poll);
        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/polls/{poll.Id}", new { id = poll.Id });
    }

    private static async Task<IResult> OpenAsync(
        Guid id,
        OpenPollRequest request,
        NoticeDbContext context,
        IOutbox outbox,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var poll = await context.Polls
            .Include(p => p.Options)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (poll is null)
        {
            return Error.NotFound("poll.not_found", "No such poll.").ToProblem();
        }

        var now = timeProvider.GetUtcNow();

        var result = poll.Open(
            now, request.ClosesAtUtc, request.EligibleFlatCount, request.QuorumPercent);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        outbox.Enqueue(
            new PollOpened
            {
                SocietyId = poll.SocietyId,
                PollId = poll.Id,
                Question = poll.QuestionEn,
                Kind = poll.Kind.ToString(),
                ClosesAtUtc = request.ClosesAtUtc,
                EligibleFlatCount = request.EligibleFlatCount,
                OccurredAtUtc = now,
            });

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { id = poll.Id, closesAtUtc = poll.ClosesAtUtc });
    }

    private static async Task<IResult> VoteAsync(
        Guid id,
        CastVoteRequest request,
        NoticeDbContext context,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var poll = await context.Polls
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (poll is null)
        {
            return Error.NotFound("poll.not_found", "No such poll.").ToProblem();
        }

        var result = poll.CastVote(
            request.FlatId,
            currentUser.RequireUserId(),
            request.OptionId,
            timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CloseAsync(
        Guid id,
        NoticeDbContext context,
        IOutbox outbox,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var poll = await context.Polls
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (poll is null)
        {
            return Error.NotFound("poll.not_found", "No such poll.").ToProblem();
        }

        var now = timeProvider.GetUtcNow();
        var result = poll.Close(now);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        var tally = poll.Tally(now);

        // A winner only when quorum was reached and one option is strictly ahead. A tie has no
        // winner, and reporting the first of two equal options as one would be a quiet lie in
        // a record a society may have to defend.
        var ordered = tally.Options.OrderByDescending(o => o.VoteCount).ToList();

        var winner = tally.QuorumMet
                     && ordered.Count > 0
                     && (ordered.Count == 1 || ordered[0].VoteCount > ordered[1].VoteCount)
            ? ordered[0].LabelEn
            : null;

        outbox.Enqueue(
            new PollClosed
            {
                SocietyId = poll.SocietyId,
                PollId = poll.Id,
                Question = poll.QuestionEn,
                Turnout = tally.Turnout,
                EligibleFlatCount = tally.EligibleFlatCount,
                QuorumMet = tally.QuorumMet,
                WinningOption = winner,
                OccurredAtUtc = now,
            });

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(tally);
    }

    private static async Task<IResult> ListAsync(
        NoticeDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-30);

        var polls = await context.Polls
            .AsNoTracking()
            .Include(p => p.Options)
            .Where(p => p.Status == PollStatus.Open
                        || (p.Status == PollStatus.Closed && p.ClosesAtUtc > cutoff))
            .OrderByDescending(p => p.Status == PollStatus.Open)
            .ThenByDescending(p => p.ClosesAtUtc)
            .Select(p => new
            {
                p.Id,
                Kind = p.Kind.ToString(),
                Status = p.Status.ToString(),
                p.QuestionEn,
                p.QuestionHi,
                p.ClosesAtUtc,
                p.NoticeId,
                Options = p.Options
                    .OrderBy(o => o.Position)
                    .Select(o => new { o.Id, o.LabelEn, o.LabelHi }),
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(polls);
    }

    private static async Task<IResult> ResultAsync(
        Guid id,
        NoticeDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var poll = await context.Polls
            .AsNoTracking()
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return poll is null
            ? Error.NotFound("poll.not_found", "No such poll.").ToProblem()
            : Results.Ok(poll.Tally(timeProvider.GetUtcNow()));
    }
}
