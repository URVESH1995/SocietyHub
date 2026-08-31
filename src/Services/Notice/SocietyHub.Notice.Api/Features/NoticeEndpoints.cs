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
using SocietyHub.SharedKernel.Globalization;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Notice.Api.Features;

public sealed record CreateNoticeRequest(
    NoticeCategory Category,
    string TitleEn,
    string BodyEn,
    string? TitleHi,
    string? BodyHi,
    NoticeAudience Audience,
    IReadOnlyList<string>? TargetTowers,
    IReadOnlyList<Guid>? TargetFlatIds,
    bool RequiresAcknowledgement,
    bool IsPinned);

public sealed record PublishNoticeRequest(DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// What a resident sees. Title and body are already resolved to their language here rather
/// than shipping both and letting the client choose — that choice is made once, on the server,
/// and every client gets it right for free.
/// </summary>
public sealed record NoticeView(
    Guid Id,
    string Category,
    string Title,
    string Body,
    string AuthorName,
    bool IsPinned,
    bool RequiresAcknowledgement,
    bool HasAcknowledged,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed class CreateNoticeValidator : AbstractValidator<CreateNoticeRequest>
{
    public CreateNoticeValidator()
    {
        RuleFor(r => r.TitleEn)
            .NotEmpty().WithErrorCode("Notice.TitleRequired")
            .MaximumLength(300).WithErrorCode("Notice.TitleTooLong");

        RuleFor(r => r.BodyEn)
            .NotEmpty().WithErrorCode("Notice.BodyRequired")
            .MaximumLength(8000).WithErrorCode("Notice.BodyTooLong");

        // A targeted notice with no targets reaches nobody, and does so silently — the author
        // sees it published and assumes it went out.
        RuleFor(r => r.TargetTowers)
            .NotEmpty().When(r => r.Audience == NoticeAudience.Towers)
            .WithErrorCode("Notice.TowersRequired");

        RuleFor(r => r.TargetFlatIds)
            .NotEmpty().When(r => r.Audience == NoticeAudience.Flats)
            .WithErrorCode("Notice.FlatsRequired");

        // Hindi is all-or-nothing. Half a translation is worse than none: a resident gets a
        // Hindi title over an English body and cannot tell whether they missed something.
        RuleFor(r => r.BodyHi)
            .NotEmpty().When(r => !string.IsNullOrWhiteSpace(r.TitleHi))
            .WithErrorCode("Notice.HindiBodyRequired");

        RuleFor(r => r.TitleHi)
            .NotEmpty().When(r => !string.IsNullOrWhiteSpace(r.BodyHi))
            .WithErrorCode("Notice.HindiTitleRequired");
    }
}

public static class NoticeEndpoints
{
    /// <summary>
    /// A cap on pinned notices. A board where everything is pinned is a board where nothing is,
    /// and the first thing a busy secretary does is pin their own notice.
    /// </summary>
    public const int MaxPinnedNotices = 3;

    /// <summary>
    /// How much of a notice travels on the event. Enough for a lock screen, and no more — the
    /// body runs to 8,000 characters and a fan-out to 600 residents should not push that
    /// through the broker to render one line.
    /// </summary>
    private const int SummaryLength = 140;

    private static string Summarise(string body)
    {
        var flattened = body.ReplaceLineEndings(" ").Trim();

        return flattened.Length <= SummaryLength
            ? flattened
            : flattened[..SummaryLength].TrimEnd() + "…";
    }

    public static IEndpointRouteBuilder MapNoticeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notices").WithTags("Notices");

        group.MapPost("/", CreateAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .WithValidation<CreateNoticeRequest>()
             .WithSummary("Drafts a notice. Nothing is sent until it is published.");

        group.MapPost("/{id:guid}/publish", PublishAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .WithSummary("Publishes a notice and notifies its audience.");

        group.MapPost("/{id:guid}/withdraw", WithdrawAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .WithSummary("Withdraws a published notice, keeping the record of it.");

        group.MapGet("/", FeedAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("The board, filtered to what reaches the caller, in their language.");

        group.MapPost("/{id:guid}/acknowledge", AcknowledgeAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Records that the caller has read a notice that asks for it.");

        group.MapGet("/{id:guid}/acknowledgements", AcknowledgementsAsync)
             .RequireAuthorization(SocietyHubPolicies.CommitteeDecisions)
             .WithSummary("Who has confirmed reading a notice, for the committee's records.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateNoticeRequest request,
        NoticeDbContext context,
        ITenantContext tenant,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();

        if (request.IsPinned)
        {
            var pinned = await context.Notices
                .CountAsync(
                    n => n.IsPinned && n.Status == NoticeStatus.Published,
                    cancellationToken);

            if (pinned >= MaxPinnedNotices)
            {
                return Error.Conflict(
                        "notice.too_many_pinned",
                        $"At most {MaxPinnedNotices} notices can be pinned. Unpin one first.")
                    .ToProblem();
            }
        }

        var notice = new Domain.Notice(
            Guid.CreateVersion7(),
            societyId,
            currentUser.RequireUserId(),
            currentUser.Email ?? "Committee",
            request.Category,
            request.TitleEn,
            request.BodyEn,
            timeProvider.GetUtcNow());

        if (request.TitleHi is not null && request.BodyHi is not null)
        {
            notice.SetHindi(request.TitleHi, request.BodyHi);
        }

        switch (request.Audience)
        {
            case NoticeAudience.Towers:
                notice.TargetTowersNamed(request.TargetTowers ?? []);
                break;
            case NoticeAudience.Flats:
                notice.TargetFlats(request.TargetFlatIds ?? []);
                break;
            case NoticeAudience.Committee:
                notice.TargetCommittee();
                break;
        }

        if (request.RequiresAcknowledgement)
        {
            notice.RequireAcknowledgement();
        }

        notice.Pin(request.IsPinned);

        context.Notices.Add(notice);
        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/notices/{notice.Id}", new { id = notice.Id });
    }

    private static async Task<IResult> PublishAsync(
        Guid id,
        PublishNoticeRequest request,
        NoticeDbContext context,
        IOutbox outbox,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var notice = await context.Notices
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (notice is null)
        {
            return Error.NotFound("notice.not_found", "No such notice.").ToProblem();
        }

        var now = timeProvider.GetUtcNow();
        var result = notice.Publish(now, request.ExpiresAtUtc);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        // Staged in the same transaction as the publish. Either the notice is up and the
        // event will go out, or neither happened — a notice nobody was told about is the
        // failure this whole outbox exists to prevent.
        outbox.Enqueue(
            new NoticePublished
            {
                SocietyId = notice.SocietyId,
                NoticeId = notice.Id,
                Category = notice.Category.ToString(),
                Title = notice.TitleEn,
                Summary = Summarise(notice.BodyEn),
                Audience = notice.Audience.ToString(),
                TargetTowers = notice.TargetTowers,
                TargetFlatIds = notice.TargetFlatIds,
                RequiresAcknowledgement = notice.RequiresAcknowledgement,
                ExpiresAtUtc = notice.ExpiresAtUtc,
                OccurredAtUtc = now,
            });

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { id = notice.Id, publishedAtUtc = notice.PublishedAtUtc });
    }

    private static async Task<IResult> WithdrawAsync(
        Guid id,
        NoticeDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var notice = await context.Notices
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (notice is null)
        {
            return Error.NotFound("notice.not_found", "No such notice.").ToProblem();
        }

        var result = notice.Withdraw(timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> FeedAsync(
        string? tower,
        Guid? flatId,
        NoticeDbContext context,
        ICurrentUser currentUser,
        ILocaleContext locale,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var userId = currentUser.RequireUserId();
        var isCommittee = currentUser.IsInRole(SocietyHubRoles.CommitteeMember)
                          || currentUser.IsInRole(SocietyHubRoles.SocietyAdmin);

        // Narrowed in the database to what is current, then filtered in memory for audience.
        // The audience rule lives on the aggregate and is not translatable to SQL; the result
        // set is a few dozen rows per society, so this is cheap and stays correct in one place.
        var candidates = await context.Notices
            .AsNoTracking()
            .Where(n => n.Status == NoticeStatus.Published
                        && (n.ExpiresAtUtc == null || n.ExpiresAtUtc > now))
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.PublishedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);

        var visible = candidates
            .Where(n => n.Reaches(tower, flatId, isCommittee))
            .ToList();

        var acknowledged = await context.NoticeAcknowledgements
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.NoticeId)
            .ToListAsync(cancellationToken);

        var wantsHindi = locale.Language.Value.StartsWith("hi", StringComparison.OrdinalIgnoreCase);

        var views = visible.Select(n => new NoticeView(
            n.Id,
            n.Category.ToString(),

            // Falls back to English when a Hindi translation is missing. Showing the notice in
            // the wrong language is bad; showing nothing at all is worse.
            wantsHindi && n.TitleHi is not null ? n.TitleHi : n.TitleEn,
            wantsHindi && n.BodyHi is not null ? n.BodyHi : n.BodyEn,
            n.AuthorName,
            n.IsPinned,
            n.RequiresAcknowledgement,
            acknowledged.Contains(n.Id),
            n.PublishedAtUtc,
            n.ExpiresAtUtc));

        return Results.Ok(views);
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid id,
        NoticeDbContext context,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var notice = await context.Notices
            .Include(n => n.Acknowledgements)
            .FirstOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (notice is null)
        {
            return Error.NotFound("notice.not_found", "No such notice.").ToProblem();
        }

        var result = notice.Acknowledge(currentUser.RequireUserId(), timeProvider.GetUtcNow());

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AcknowledgementsAsync(
        Guid id,
        NoticeDbContext context,
        CancellationToken cancellationToken)
    {
        var rows = await context.NoticeAcknowledgements
            .AsNoTracking()
            .Where(a => a.NoticeId == id)
            .OrderBy(a => a.AcknowledgedAtUtc)
            .Select(a => new { a.UserId, a.AcknowledgedAtUtc })
            .ToListAsync(cancellationToken);

        return Results.Ok(new { noticeId = id, count = rows.Count, acknowledgements = rows });
    }
}
