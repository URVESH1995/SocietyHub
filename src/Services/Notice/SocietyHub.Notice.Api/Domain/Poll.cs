using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Notice.Api.Domain;

public enum PollStatus
{
    Draft = 0,
    Open = 1,
    Closed = 2,
    Cancelled = 3,
}

/// <summary>
/// How much a poll is worth. The distinction is not cosmetic — an Opinion poll is a suggestion
/// box, a Resolution is a record a society may have to produce for its auditor or for a member
/// who disputes the outcome, and the rules below are stricter for the second.
/// </summary>
public enum PollKind
{
    Opinion = 0,
    Resolution = 1,
}

/// <summary>
/// A society vote. Deliberately one vote per flat rather than per person: maintenance,
/// resolutions and levies attach to the flat, and letting a four-adult household outvote a
/// two-adult one on a shared bill is the first thing a losing side will challenge.
/// </summary>
public sealed class Poll : AggregateRoot, ITenantScoped, IAuditable
{
    private readonly List<PollOption> _options = [];
    private readonly List<PollVote> _votes = [];

    private Poll() { }

    public Poll(
        Guid id,
        Guid societyId,
        Guid createdByUserId,
        PollKind kind,
        string questionEn,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        AuthorUserId = createdByUserId;
        Kind = kind;
        QuestionEn = questionEn;
        Status = PollStatus.Draft;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public Guid AuthorUserId { get; private set; }

    public PollKind Kind { get; private set; }

    public PollStatus Status { get; private set; }

    public string QuestionEn { get; private set; } = string.Empty;

    public string? QuestionHi { get; private set; }

    /// <summary>
    /// Optional link to the notice that announced it, so a resident reading the notice can vote
    /// without hunting for the poll.
    /// </summary>
    public Guid? NoticeId { get; private set; }

    /// <summary>
    /// Whether votes are hidden until the poll closes. On by default for a Resolution: a running
    /// tally on a contested vote changes how people vote, and for anything binding that is a
    /// defect rather than a feature.
    /// </summary>
    public bool ResultsHiddenUntilClose { get; private set; }

    /// <summary>
    /// The share of eligible flats that must vote for the result to count. Zero means an
    /// advisory poll with no threshold.
    /// </summary>
    public int QuorumPercent { get; private set; }

    /// <summary>
    /// Eligible flats at the moment the poll opened, frozen then rather than counted at close.
    /// If a flat is sold mid-vote, the denominator must not move under the result.
    /// </summary>
    public int EligibleFlatCount { get; private set; }

    public DateTimeOffset? OpensAtUtc { get; private set; }

    public DateTimeOffset? ClosesAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<PollOption> Options => _options;

    public IReadOnlyCollection<PollVote> Votes => _votes;

    public void SetHindi(string questionHi) => QuestionHi = questionHi;

    public void LinkToNotice(Guid noticeId) => NoticeId = noticeId;

    public Result AddOption(string labelEn, string? labelHi)
    {
        if (Status is not PollStatus.Draft)
        {
            return Error.Conflict(
                "poll.not_draft", "Options can only be changed before a poll opens.");
        }

        if (_options.Count >= 10)
        {
            return Error.Validation("poll.too_many_options", "A poll may have at most 10 options.");
        }

        _options.Add(new PollOption(
            Guid.CreateVersion7(), SocietyId, Id, _options.Count, labelEn, labelHi));

        return Result.Success();
    }

    public Result Open(
        DateTimeOffset nowUtc,
        DateTimeOffset closesAtUtc,
        int eligibleFlatCount,
        int quorumPercent)
    {
        if (Status is not PollStatus.Draft)
        {
            return Error.Conflict("poll.already_open", "This poll has already been opened.");
        }

        if (_options.Count < 2)
        {
            return Error.Validation("poll.needs_options", "A poll needs at least two options.");
        }

        if (closesAtUtc <= nowUtc)
        {
            return Error.Validation("poll.closes_in_past", "A poll cannot close before it opens.");
        }

        if (quorumPercent is < 0 or > 100)
        {
            return Error.Validation(
                "poll.invalid_quorum", "Quorum must be between 0 and 100 percent.");
        }

        if (eligibleFlatCount <= 0)
        {
            return Error.Validation(
                "poll.no_eligible_flats", "A poll needs at least one eligible flat.");
        }

        // A binding vote with a running tally is a vote you can influence by voting late.
        if (Kind is PollKind.Resolution)
        {
            ResultsHiddenUntilClose = true;
        }

        Status = PollStatus.Open;
        OpensAtUtc = nowUtc;
        ClosesAtUtc = closesAtUtc;
        EligibleFlatCount = eligibleFlatCount;
        QuorumPercent = quorumPercent;
        return Result.Success();
    }

    public bool IsOpenAt(DateTimeOffset nowUtc) =>
        Status == PollStatus.Open && ClosesAtUtc is not null && ClosesAtUtc > nowUtc;

    /// <summary>
    /// Cast or change a vote. One per flat; a resident may change their mind while the poll is
    /// open, because the alternative is a support ticket for every mis-tap.
    /// </summary>
    public Result CastVote(Guid flatId, Guid voterUserId, Guid optionId, DateTimeOffset nowUtc)
    {
        if (!IsOpenAt(nowUtc))
        {
            return Error.Conflict("poll.not_open", "This poll is not open for voting.");
        }

        if (_options.All(o => o.Id != optionId))
        {
            return Error.Validation("poll.unknown_option", "That option is not on this poll.");
        }

        var existing = _votes.FirstOrDefault(v => v.FlatId == flatId);

        if (existing is not null)
        {
            existing.ChangeTo(optionId, voterUserId, nowUtc);
            return Result.Success();
        }

        _votes.Add(new PollVote(
            Guid.CreateVersion7(), SocietyId, Id, flatId, voterUserId, optionId, nowUtc));

        return Result.Success();
    }

    public Result Close(DateTimeOffset nowUtc)
    {
        if (Status is not PollStatus.Open)
        {
            return Error.Conflict("poll.not_open", "Only an open poll can be closed.");
        }

        Status = PollStatus.Closed;
        ClosesAtUtc = nowUtc;
        ModifiedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>
    /// The tally, and whether it counts.
    ///
    /// Quorum is reported separately from the winner rather than folded into it, because a
    /// society still wants to see a failed vote's numbers — that is how it decides whether to
    /// run it again.
    /// </summary>
    public PollResult Tally(DateTimeOffset nowUtc)
    {
        var counts = _options
            .OrderBy(o => o.Position)
            .Select(o => new PollOptionResult(
                o.Id,
                o.LabelEn,
                o.LabelHi,
                _votes.Count(v => v.OptionId == o.Id)))
            .ToList();

        var turnout = _votes.Count;

        // Integer arithmetic deliberately: 50 of 100 flats against a 50% quorum must pass, and
        // floating point is how that becomes a support ticket about a vote that "should have
        // counted".
        var quorumMet = EligibleFlatCount > 0
                        && turnout * 100 >= QuorumPercent * EligibleFlatCount;

        var visible = !ResultsHiddenUntilClose || Status is PollStatus.Closed || !IsOpenAt(nowUtc);

        return new PollResult(
            Id,
            Status,
            visible ? counts : [.. counts.Select(c => c with { VoteCount = 0 })],
            visible,
            turnout,
            EligibleFlatCount,
            QuorumPercent,
            quorumMet);
    }
}

public sealed class PollOption : Entity, ITenantScoped
{
    private PollOption() { }

    public PollOption(
        Guid id, Guid societyId, Guid pollId, int position, string labelEn, string? labelHi)
        : base(id)
    {
        SocietyId = societyId;
        PollId = pollId;
        Position = position;
        LabelEn = labelEn;
        LabelHi = labelHi;
    }

    public Guid SocietyId { get; private set; }

    public Guid PollId { get; private set; }

    public int Position { get; private set; }

    public string LabelEn { get; private set; } = string.Empty;

    public string? LabelHi { get; private set; }
}

public sealed class PollVote : Entity, ITenantScoped
{
    private PollVote() { }

    public PollVote(
        Guid id,
        Guid societyId,
        Guid pollId,
        Guid flatId,
        Guid voterUserId,
        Guid optionId,
        DateTimeOffset castAtUtc)
        : base(id)
    {
        SocietyId = societyId;
        PollId = pollId;
        FlatId = flatId;
        VoterUserId = voterUserId;
        OptionId = optionId;
        CastAtUtc = castAtUtc;
    }

    public Guid SocietyId { get; private set; }

    public Guid PollId { get; private set; }

    /// <summary>The vote belongs to the flat, not to whoever happened to cast it.</summary>
    public Guid FlatId { get; private set; }

    public Guid VoterUserId { get; private set; }

    public Guid OptionId { get; private set; }

    public DateTimeOffset CastAtUtc { get; private set; }

    public DateTimeOffset? ChangedAtUtc { get; private set; }

    /// <summary>
    /// How many times this flat changed its mind. Kept because a resolution whose votes flipped
    /// repeatedly in the final hour is the one that gets disputed.
    /// </summary>
    public int ChangeCount { get; private set; }

    internal void ChangeTo(Guid optionId, Guid voterUserId, DateTimeOffset nowUtc)
    {
        if (OptionId == optionId)
        {
            return;
        }

        OptionId = optionId;
        VoterUserId = voterUserId;
        ChangedAtUtc = nowUtc;
        ChangeCount++;
    }
}

public sealed record PollOptionResult(
    Guid OptionId, string LabelEn, string? LabelHi, int VoteCount);

public sealed record PollResult(
    Guid PollId,
    PollStatus Status,
    IReadOnlyList<PollOptionResult> Options,
    bool ResultsVisible,
    int Turnout,
    int EligibleFlatCount,
    int QuorumPercent,
    bool QuorumMet);
