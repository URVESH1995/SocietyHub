using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Notice.Api.Domain;

public enum NoticeCategory
{
    General = 0,
    Maintenance = 1,
    Emergency = 2,
    Event = 3,
    Financial = 4,
    Governance = 5,
}

public enum NoticeStatus
{
    Draft = 0,
    Published = 1,
    Expired = 2,
    Withdrawn = 3,
}

/// <summary>
/// Who a notice reaches. A lift shutdown in Tower B is noise to Tower A, and a society that
/// notifies everyone about everything trains its residents to ignore notices — which is exactly
/// the state you do not want them in when the notice is about a water cut.
/// </summary>
public enum NoticeAudience
{
    /// <summary>Everyone in the society.</summary>
    Everyone = 0,

    /// <summary>Specific towers, listed in <see cref="Notice.TargetTowers"/>.</summary>
    Towers = 1,

    /// <summary>Specific flats, listed in <see cref="Notice.TargetFlatIds"/>.</summary>
    Flats = 2,

    /// <summary>Committee members only — used for governance items before they go public.</summary>
    Committee = 3,
}

/// <summary>
/// A society announcement. Bilingual by construction: a notice carries both an English and a
/// Hindi body, and a resident sees whichever matches their language.
/// </summary>
public sealed class Notice : AggregateRoot, ITenantScoped, IAuditable
{
    private readonly List<NoticeAcknowledgement> _acknowledgements = [];

    private Notice() { }

    public Notice(
        Guid id,
        Guid societyId,
        Guid authorUserId,
        string authorName,
        NoticeCategory category,
        string titleEn,
        string bodyEn,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        AuthorUserId = authorUserId;
        AuthorName = authorName;
        Category = category;
        TitleEn = titleEn;
        BodyEn = bodyEn;
        Status = NoticeStatus.Draft;
        Audience = NoticeAudience.Everyone;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public Guid AuthorUserId { get; private set; }

    public string AuthorName { get; private set; } = string.Empty;

    public NoticeCategory Category { get; private set; }

    public NoticeStatus Status { get; private set; }

    public NoticeAudience Audience { get; private set; }

    /// <summary>Comma-separated tower names when <see cref="Audience"/> is Towers.</summary>
    public string? TargetTowers { get; private set; }

    /// <summary>Comma-separated flat ids when <see cref="Audience"/> is Flats.</summary>
    public string? TargetFlatIds { get; private set; }

    public string TitleEn { get; private set; } = string.Empty;

    public string BodyEn { get; private set; } = string.Empty;

    public string? TitleHi { get; private set; }

    public string? BodyHi { get; private set; }

    /// <summary>
    /// Pinned notices sit above the feed regardless of date. A cap is enforced at the service
    /// level, because a board where everything is pinned is a board where nothing is.
    /// </summary>
    public bool IsPinned { get; private set; }

    /// <summary>
    /// Whether residents must confirm they have read it. Used for things a society may later
    /// need to prove it communicated — a rule change, a levy, an AGM notice.
    /// </summary>
    public bool RequiresAcknowledgement { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    /// <summary>
    /// When the notice stops being current. A water cut on Tuesday is clutter by Thursday, and
    /// asking a secretary to come back and delete it never happens.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<NoticeAcknowledgement> Acknowledgements => _acknowledgements;

    public void SetHindi(string titleHi, string bodyHi)
    {
        TitleHi = titleHi;
        BodyHi = bodyHi;
    }

    public void TargetTowersNamed(IEnumerable<string> towers)
    {
        Audience = NoticeAudience.Towers;
        TargetTowers = string.Join(',', towers.Select(t => t.Trim()).Where(t => t.Length > 0));
    }

    public void TargetFlats(IEnumerable<Guid> flatIds)
    {
        Audience = NoticeAudience.Flats;
        TargetFlatIds = string.Join(',', flatIds);
    }

    public void TargetCommittee() => Audience = NoticeAudience.Committee;

    public void Pin(bool pinned) => IsPinned = pinned;

    public void RequireAcknowledgement() => RequiresAcknowledgement = true;

    public Result Publish(DateTimeOffset nowUtc, DateTimeOffset? expiresAtUtc)
    {
        if (Status is not NoticeStatus.Draft)
        {
            return Error.Conflict(
                "notice.already_published", "Only a draft notice can be published.");
        }

        if (expiresAtUtc is not null && expiresAtUtc <= nowUtc)
        {
            return Error.Validation(
                "notice.expiry_in_past", "A notice cannot expire before it is published.");
        }

        Status = NoticeStatus.Published;
        PublishedAtUtc = nowUtc;
        ExpiresAtUtc = expiresAtUtc;
        return Result.Success();
    }

    /// <summary>
    /// Withdrawn, not deleted. A notice that was on the board for two days was read by people,
    /// and a society that can silently erase what it announced cannot be held to it.
    /// </summary>
    public Result Withdraw(DateTimeOffset nowUtc)
    {
        if (Status is not NoticeStatus.Published)
        {
            return Error.Conflict(
                "notice.not_published", "Only a published notice can be withdrawn.");
        }

        Status = NoticeStatus.Withdrawn;
        ModifiedAtUtc = nowUtc;
        return Result.Success();
    }

    public bool IsVisibleAt(DateTimeOffset nowUtc) =>
        Status == NoticeStatus.Published
        && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);

    /// <summary>
    /// Whether this notice reaches a particular resident. Kept on the aggregate rather than in a
    /// query so the same rule governs the feed, the notification fan-out and the read receipts —
    /// three places that would otherwise drift apart.
    /// </summary>
    public bool Reaches(string? tower, Guid? flatId, bool isCommitteeMember) => Audience switch
    {
        NoticeAudience.Everyone => true,

        NoticeAudience.Committee => isCommitteeMember,

        NoticeAudience.Towers =>
            tower is not null
            && TargetTowers is not null
            && TargetTowers.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(t => string.Equals(t.Trim(), tower, StringComparison.OrdinalIgnoreCase)),

        NoticeAudience.Flats =>
            flatId is not null
            && TargetFlatIds is not null
            && TargetFlatIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(id => Guid.TryParse(id, out var parsed) && parsed == flatId),

        _ => false,
    };

    public Result Acknowledge(Guid userId, DateTimeOffset nowUtc)
    {
        if (!RequiresAcknowledgement)
        {
            return Error.Validation(
                "notice.no_acknowledgement", "This notice does not ask for acknowledgement.");
        }

        if (_acknowledgements.Any(a => a.UserId == userId))
        {
            // Acknowledging twice is not an error worth surfacing to a resident who tapped
            // twice on a slow connection.
            return Result.Success();
        }

        _acknowledgements.Add(new NoticeAcknowledgement(
            Guid.CreateVersion7(), SocietyId, Id, userId, nowUtc));

        return Result.Success();
    }
}

public sealed class NoticeAcknowledgement : Entity, ITenantScoped
{
    private NoticeAcknowledgement() { }

    public NoticeAcknowledgement(
        Guid id, Guid societyId, Guid noticeId, Guid userId, DateTimeOffset acknowledgedAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        NoticeId = noticeId;
        UserId = userId;
        AcknowledgedAtUtc = acknowledgedAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public Guid NoticeId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset AcknowledgedAtUtc { get; private set; }
}
