using SocietyHub.Notice.Api.Domain;

namespace SocietyHub.Notice.Tests;

/// <summary>
/// Targeting decides who is interrupted. Too wide and residents learn to ignore the board;
/// too narrow and the people who needed to know never hear.
/// </summary>
public sealed class NoticeAudienceTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AuthorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FlatA = Guid.Parse("22222222-0000-0000-0000-00000000000a");
    private static readonly Guid FlatB = Guid.Parse("22222222-0000-0000-0000-00000000000b");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static Api.Domain.Notice Notice(NoticeCategory category = NoticeCategory.General) =>
        new(Guid.CreateVersion7(),
            SocietyId,
            AuthorId,
            "Secretary",
            category,
            "Water supply interrupted",
            "Water will be off from 10am to 2pm on Tuesday.",
            Now);

    [Fact]
    public void A_notice_reaches_everyone_by_default()
    {
        // The safe default. A notice that silently reached nobody is the failure mode that
        // matters, and the author has no way to see it happened.
        Assert.True(Notice().Reaches(tower: null, flatId: null, isCommitteeMember: false));
    }

    [Fact]
    public void A_tower_notice_skips_the_other_towers()
    {
        var notice = Notice(NoticeCategory.Maintenance);
        notice.TargetTowersNamed(["B", "C"]);

        Assert.True(notice.Reaches("B", FlatA, false));
        Assert.True(notice.Reaches("C", FlatA, false));
        Assert.False(notice.Reaches("A", FlatA, false));
    }

    [Fact]
    public void Tower_matching_ignores_case_and_stray_spaces()
    {
        // Tower names are typed by a secretary into a text box, and "b " is the same tower
        // as "B" to everyone except a string comparison.
        var notice = Notice();
        notice.TargetTowersNamed([" b ", "C"]);

        Assert.True(notice.Reaches("B", null, false));
    }

    [Fact]
    public void A_flat_notice_reaches_only_the_flats_named()
    {
        var notice = Notice(NoticeCategory.Financial);
        notice.TargetFlats([FlatA]);

        Assert.True(notice.Reaches("A", FlatA, false));
        Assert.False(notice.Reaches("A", FlatB, false));
    }

    [Fact]
    public void A_committee_notice_is_not_visible_to_a_resident()
    {
        // Governance items are drafted before they go public. A resident seeing a draft
        // resolution is a leak, not a feature.
        var notice = Notice(NoticeCategory.Governance);
        notice.TargetCommittee();

        Assert.True(notice.Reaches("A", FlatA, isCommitteeMember: true));
        Assert.False(notice.Reaches("A", FlatA, isCommitteeMember: false));
    }

    [Fact]
    public void A_targeted_notice_reaches_nobody_when_the_reader_is_unplaced()
    {
        // A caller with no tower and no flat — a guard, or a token missing the claim — must
        // not receive a notice aimed at specific homes.
        var notice = Notice();
        notice.TargetTowersNamed(["A"]);

        Assert.False(notice.Reaches(tower: null, flatId: null, isCommitteeMember: false));
    }
}

/// <summary>Publication, expiry and withdrawal.</summary>
public sealed class NoticeLifecycleTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AuthorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ReaderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static Api.Domain.Notice Notice() =>
        new(Guid.CreateVersion7(),
            SocietyId,
            AuthorId,
            "Secretary",
            NoticeCategory.General,
            "Annual general meeting",
            "The AGM will be held on 20 September at 6pm in the clubhouse.",
            Now);

    [Fact]
    public void A_draft_is_not_on_the_board()
    {
        Assert.False(Notice().IsVisibleAt(Now));
    }

    [Fact]
    public void Publishing_puts_it_on_the_board()
    {
        var notice = Notice();

        Assert.True(notice.Publish(Now, expiresAtUtc: null).IsSuccess);
        Assert.Equal(NoticeStatus.Published, notice.Status);
        Assert.True(notice.IsVisibleAt(Now));
    }

    [Fact]
    public void Publishing_twice_is_rejected()
    {
        // Not idempotent on purpose: the second publish would fire a second notification to
        // every resident, and a duplicate blast to 600 people is not a harmless no-op.
        var notice = Notice();
        notice.Publish(Now, null);

        var again = notice.Publish(Now, null);

        Assert.True(again.IsFailure);
        Assert.Equal("notice.already_published", again.Error.Code);
    }

    [Fact]
    public void An_expiry_in_the_past_is_rejected()
    {
        var result = Notice().Publish(Now, expiresAtUtc: Now.AddHours(-1));

        Assert.True(result.IsFailure);
        Assert.Equal("notice.expiry_in_past", result.Error.Code);
    }

    [Fact]
    public void An_expired_notice_drops_off_the_board_on_its_own()
    {
        // A water cut on Tuesday is clutter by Thursday, and nobody ever comes back to
        // delete it.
        var notice = Notice();
        notice.Publish(Now, expiresAtUtc: Now.AddDays(2));

        Assert.True(notice.IsVisibleAt(Now.AddDays(1)));
        Assert.False(notice.IsVisibleAt(Now.AddDays(3)));
    }

    [Fact]
    public void A_withdrawn_notice_leaves_the_board_but_stays_in_the_record()
    {
        // Withdrawn, not deleted. People read it while it was up, and a society that can
        // silently erase what it announced cannot be held to it.
        var notice = Notice();
        notice.Publish(Now, null);

        Assert.True(notice.Withdraw(Now.AddHours(2)).IsSuccess);
        Assert.Equal(NoticeStatus.Withdrawn, notice.Status);
        Assert.False(notice.IsVisibleAt(Now.AddHours(3)));
    }

    [Fact]
    public void A_draft_cannot_be_withdrawn()
    {
        var result = Notice().Withdraw(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("notice.not_published", result.Error.Code);
    }

    [Fact]
    public void Acknowledgement_is_refused_when_the_notice_does_not_ask_for_it()
    {
        var result = Notice().Acknowledge(ReaderId, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("notice.no_acknowledgement", result.Error.Code);
    }

    [Fact]
    public void Acknowledging_twice_records_one_reader()
    {
        // A resident on a slow connection taps twice. That is not an error to show them, and
        // it must not inflate the count the committee relies on.
        var notice = Notice();
        notice.RequireAcknowledgement();

        Assert.True(notice.Acknowledge(ReaderId, Now).IsSuccess);
        Assert.True(notice.Acknowledge(ReaderId, Now.AddSeconds(2)).IsSuccess);

        Assert.Single(notice.Acknowledgements);
    }
}
