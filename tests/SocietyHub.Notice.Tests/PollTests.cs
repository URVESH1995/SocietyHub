using SocietyHub.Notice.Api.Domain;

namespace SocietyHub.Notice.Tests;

/// <summary>
/// A vote a society may have to defend. Every rule here exists because the losing side of a
/// contested resolution will look for exactly this kind of gap.
/// </summary>
public sealed class PollTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ChairId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Closes = Now.AddDays(7);

    private static Guid Flat(int n) => Guid.Parse($"22222222-0000-0000-0000-{n:D12}");

    private static Poll Poll(PollKind kind = PollKind.Opinion)
    {
        var poll = new Poll(
            Guid.CreateVersion7(),
            SocietyId,
            ChairId,
            kind,
            "Should the society install rooftop solar?",
            Now);

        poll.AddOption("Yes", "हाँ");
        poll.AddOption("No", "नहीं");
        return poll;
    }

    private static Poll OpenPoll(PollKind kind = PollKind.Opinion, int flats = 100, int quorum = 0)
    {
        var poll = Poll(kind);
        poll.Open(Now, Closes, flats, quorum);
        return poll;
    }

    // --- opening --------------------------------------------------------

    [Fact]
    public void A_poll_with_one_option_cannot_open()
    {
        var poll = new Poll(Guid.CreateVersion7(), SocietyId, ChairId, PollKind.Opinion, "?", Now);
        poll.AddOption("Yes", null);

        var result = poll.Open(Now, Closes, 100, 0);

        Assert.True(result.IsFailure);
        Assert.Equal("poll.needs_options", result.Error.Code);
    }

    [Fact]
    public void A_poll_cannot_close_before_it_opens()
    {
        var result = Poll().Open(Now, Now.AddHours(-1), 100, 0);

        Assert.True(result.IsFailure);
        Assert.Equal("poll.closes_in_past", result.Error.Code);
    }

    [Fact]
    public void Options_are_frozen_once_voting_starts()
    {
        // Adding a choice after people have voted invalidates every vote already cast.
        var poll = OpenPoll();

        var result = poll.AddOption("Abstain", null);

        Assert.True(result.IsFailure);
        Assert.Equal("poll.not_draft", result.Error.Code);
    }

    [Fact]
    public void The_eligible_flat_count_is_frozen_when_voting_opens()
    {
        // If a flat is sold mid-vote, the denominator must not move under the result. The
        // count is taken once, at open, and never recomputed.
        var poll = OpenPoll(flats: 100, quorum: 50);

        for (var i = 1; i <= 50; i++)
        {
            poll.CastVote(Flat(i), Guid.CreateVersion7(), poll.Options.First().Id, Now);
        }

        var tally = poll.Tally(Now);

        Assert.Equal(100, tally.EligibleFlatCount);
        Assert.True(tally.QuorumMet);
    }

    // --- voting ---------------------------------------------------------

    [Fact]
    public void One_flat_gets_one_vote_however_many_adults_live_there()
    {
        // The rule the whole design rests on. Maintenance and levies attach to the flat, and
        // a four-adult household outvoting a two-adult one on a shared bill is the first
        // thing a losing side will challenge.
        var poll = OpenPoll();
        var yes = poll.Options.First().Id;

        poll.CastVote(Flat(1), Guid.CreateVersion7(), yes, Now);
        poll.CastVote(Flat(1), Guid.CreateVersion7(), yes, Now.AddMinutes(5));
        poll.CastVote(Flat(1), Guid.CreateVersion7(), yes, Now.AddMinutes(9));

        Assert.Equal(1, poll.Tally(Now).Turnout);
    }

    [Fact]
    public void A_flat_can_change_its_mind_while_the_poll_is_open()
    {
        var poll = OpenPoll();
        var yes = poll.Options.First().Id;
        var no = poll.Options.Last().Id;

        poll.CastVote(Flat(1), ChairId, yes, Now);
        poll.CastVote(Flat(1), ChairId, no, Now.AddHours(1));

        var tally = poll.Tally(Now);

        Assert.Equal(0, tally.Options.First(o => o.OptionId == yes).VoteCount);
        Assert.Equal(1, tally.Options.First(o => o.OptionId == no).VoteCount);
        Assert.Equal(1, poll.Votes.Single().ChangeCount);
    }

    [Fact]
    public void Voting_after_the_deadline_is_refused()
    {
        var poll = OpenPoll();

        var result = poll.CastVote(
            Flat(1), ChairId, poll.Options.First().Id, Closes.AddSeconds(1));

        Assert.True(result.IsFailure);
        Assert.Equal("poll.not_open", result.Error.Code);
    }

    [Fact]
    public void Voting_on_a_draft_is_refused()
    {
        var poll = Poll();

        var result = poll.CastVote(Flat(1), ChairId, poll.Options.First().Id, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("poll.not_open", result.Error.Code);
    }

    [Fact]
    public void An_option_from_another_poll_is_refused()
    {
        var poll = OpenPoll();
        var otherPollsOption = OpenPoll().Options.First().Id;

        var result = poll.CastVote(Flat(1), ChairId, otherPollsOption, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("poll.unknown_option", result.Error.Code);
    }

    // --- quorum ---------------------------------------------------------

    [Fact]
    public void Exactly_meeting_quorum_counts()
    {
        // 50 of 100 against a 50% threshold. Integer arithmetic deliberately — floating point
        // is how this becomes a support ticket about a vote that should have counted.
        var poll = OpenPoll(flats: 100, quorum: 50);

        for (var i = 1; i <= 50; i++)
        {
            poll.CastVote(Flat(i), Guid.CreateVersion7(), poll.Options.First().Id, Now);
        }

        Assert.True(poll.Tally(Now).QuorumMet);
    }

    [Fact]
    public void One_vote_short_of_quorum_does_not_count()
    {
        var poll = OpenPoll(flats: 100, quorum: 50);

        for (var i = 1; i <= 49; i++)
        {
            poll.CastVote(Flat(i), Guid.CreateVersion7(), poll.Options.First().Id, Now);
        }

        var tally = poll.Tally(Now);

        Assert.False(tally.QuorumMet);

        // The numbers are still reported. A society decides whether to re-run a failed vote
        // by looking at how close it came.
        Assert.Equal(49, tally.Turnout);
    }

    [Fact]
    public void A_poll_with_no_quorum_requirement_always_counts()
    {
        Assert.True(OpenPoll(flats: 100, quorum: 0).Tally(Now).QuorumMet);
    }

    // --- result visibility ----------------------------------------------

    [Fact]
    public void An_opinion_poll_shows_a_running_tally()
    {
        var poll = OpenPoll(PollKind.Opinion);
        poll.CastVote(Flat(1), ChairId, poll.Options.First().Id, Now);

        var tally = poll.Tally(Now);

        Assert.True(tally.ResultsVisible);
        Assert.Equal(1, tally.Options.First().VoteCount);
    }

    [Fact]
    public void A_resolution_hides_its_counts_until_it_closes()
    {
        // A running tally on a binding vote is a vote you can influence by voting late.
        var poll = OpenPoll(PollKind.Resolution);
        poll.CastVote(Flat(1), ChairId, poll.Options.First().Id, Now);

        var during = poll.Tally(Now);

        Assert.False(during.ResultsVisible);
        Assert.All(during.Options, o => Assert.Equal(0, o.VoteCount));

        // Turnout is still visible while it runs — knowing how many have voted does not
        // reveal which way, and a committee needs it to chase people before the deadline.
        Assert.Equal(1, during.Turnout);

        poll.Close(Now.AddDays(1));
        var after = poll.Tally(Now.AddDays(1));

        Assert.True(after.ResultsVisible);
        Assert.Equal(1, after.Options.First().VoteCount);
    }

    [Fact]
    public void A_resolution_that_has_run_out_of_time_reveals_its_counts()
    {
        // Even before someone gets around to pressing Close. Otherwise the result is hidden
        // for as long as the committee is slow.
        var poll = OpenPoll(PollKind.Resolution);
        poll.CastVote(Flat(1), ChairId, poll.Options.First().Id, Now);

        Assert.True(poll.Tally(Closes.AddMinutes(1)).ResultsVisible);
    }

    [Fact]
    public void Closing_a_draft_is_refused()
    {
        var result = Poll().Close(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("poll.not_open", result.Error.Code);
    }
}
