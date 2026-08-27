using SocietyHub.Helpdesk.Api.Domain;
using SocietyHub.Helpdesk.Api.Features;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Helpdesk.Tests;

/// <summary>
/// The ticket lifecycle, and the rules that stop the SLA metric being gamed.
/// </summary>
public sealed class ComplaintTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FlatId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ResidentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PlumberId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTimeOffset Raised = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = Raised.AddHours(24);

    private static Complaint Complaint() =>
        new(Guid.CreateVersion7(),
            SocietyId,
            FlatId,
            ResidentId,
            "CMP-2026-00412",
            ComplaintCategory.Plumbing,
            ComplaintPriority.Normal,
            "Kitchen tap leaking",
            "The mixer tap drips constantly and the cabinet below is damp.",
            Raised,
            Due);

    [Fact]
    public void A_new_complaint_is_open_and_unassigned()
    {
        var complaint = Complaint();

        Assert.Equal(ComplaintStatus.Open, complaint.Status);
        Assert.True(complaint.IsOpen);
        Assert.Null(complaint.AssignedToUserId);
        Assert.Equal(0, complaint.EscalationLevel);
    }

    [Fact]
    public void Assignment_moves_it_to_assigned()
    {
        var complaint = Complaint();

        var result = complaint.Assign(PlumberId, "Suresh (plumber)", Raised.AddMinutes(20));

        Assert.True(result.IsSuccess);
        Assert.Equal(ComplaintStatus.Assigned, complaint.Status);
        Assert.Equal("Suresh (plumber)", complaint.AssignedToName);
    }

    [Fact]
    public void Starting_work_records_the_first_response()
    {
        var complaint = Complaint();
        complaint.Assign(PlumberId, "Suresh", Raised.AddMinutes(20));

        complaint.Start(Raised.AddHours(1));

        Assert.Equal(ComplaintStatus.InProgress, complaint.Status);
        Assert.Equal(Raised.AddHours(1), complaint.FirstResponseAtUtc);
    }

    [Fact]
    public void Resolving_requires_a_description_of_what_was_done()
    {
        // "Fixed" gives the resident nothing to verify against and the next person to hit the
        // same fault nothing to learn from.
        var complaint = Complaint();

        var result = complaint.Resolve("   ", Raised.AddHours(3));

        Assert.True(result.IsFailure);
        Assert.Equal("Complaint.ResolutionRequired", result.Error.Code);
        Assert.Equal(ComplaintStatus.Open, complaint.Status);
    }

    [Fact]
    public void Resolving_within_the_window_is_not_a_breach()
    {
        var complaint = Complaint();
        complaint.Resolve("Replaced the cartridge and reseated the washer.", Raised.AddHours(3));

        Assert.Equal(ComplaintStatus.Resolved, complaint.Status);
        Assert.False(complaint.HasBreachedSla(Raised.AddDays(5)));
        Assert.Equal(TimeSpan.FromHours(3), complaint.TimeToResolve);
    }

    [Fact]
    public void Breach_is_judged_against_resolution_not_closure()
    {
        // Closure waits on the resident, who may be travelling. Holding the society to a
        // deadline that depends on a resident replying would measure the wrong party.
        var complaint = Complaint();
        complaint.Resolve("Replaced the cartridge.", Raised.AddHours(3));

        // Resident confirms a week later. Still not a breach.
        complaint.Close(5, "Quick work, thanks.", Raised.AddDays(7));

        Assert.False(complaint.HasBreachedSla(Raised.AddDays(7)));
    }

    [Fact]
    public void An_unresolved_complaint_breaches_once_the_deadline_passes()
    {
        var complaint = Complaint();

        Assert.False(complaint.HasBreachedSla(Due.AddMinutes(-1)));
        Assert.True(complaint.HasBreachedSla(Due.AddMinutes(1)));
    }

    [Fact]
    public void Only_the_resident_who_raised_it_can_rate_it()
    {
        // Enforced at the endpoint, but the rating itself is bounded here.
        var complaint = Complaint();
        complaint.Resolve("Replaced the cartridge.", Raised.AddHours(3));

        var tooHigh = complaint.Close(6, null, Raised.AddHours(4));

        Assert.True(tooHigh.IsFailure);
        Assert.Equal(ErrorType.Validation, tooHigh.Error.Type);
    }

    [Fact]
    public void A_complaint_cannot_be_closed_before_it_is_resolved()
    {
        var complaint = Complaint();

        var result = complaint.Close(5, null, Raised.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal("Complaint.NotResolved", result.Error.Code);
    }

    [Fact]
    public void Closing_without_a_rating_is_allowed()
    {
        // Forcing a rating would make residents click a star to dismiss a dialog, and the
        // resulting numbers would mean nothing.
        var complaint = Complaint();
        complaint.Resolve("Replaced the cartridge.", Raised.AddHours(3));

        Assert.True(complaint.Close(null, null, Raised.AddHours(4)).IsSuccess);
        Assert.Null(complaint.SatisfactionRating);
    }

    [Fact]
    public void Reopening_does_not_reset_the_deadline()
    {
        // The rule that stops the metric being gamed. If reopening granted a fresh window, a
        // premature "resolved" would buy another 24 hours every time.
        var complaint = Complaint();
        complaint.Resolve("Tightened the washer.", Raised.AddHours(3));

        var originalDue = complaint.SlaDueAtUtc;
        complaint.Reopen("Still dripping the next morning.", Raised.AddHours(20));

        Assert.Equal(originalDue, complaint.SlaDueAtUtc);
        Assert.Equal(ComplaintStatus.InProgress, complaint.Status);
        Assert.Null(complaint.ResolvedAtUtc);
    }

    [Fact]
    public void Reopening_records_the_reason_as_a_visible_note()
    {
        var complaint = Complaint();
        complaint.Resolve("Tightened the washer.", Raised.AddHours(3));
        complaint.Reopen("Still dripping.", Raised.AddHours(20));

        var note = Assert.Single(complaint.Notes);
        Assert.Contains("Still dripping", note.Body);
        Assert.False(note.IsInternalOnly);
    }

    [Fact]
    public void Only_a_resolved_complaint_can_be_reopened()
    {
        var complaint = Complaint();

        Assert.True(complaint.Reopen("Nope.", Raised.AddHours(1)).IsFailure);
    }

    [Fact]
    public void A_rejected_complaint_is_no_longer_open()
    {
        var complaint = Complaint();

        complaint.Reject("Duplicate of CMP-2026-00408.", Raised.AddHours(1));

        Assert.Equal(ComplaintStatus.Rejected, complaint.Status);
        Assert.False(complaint.IsOpen);
        Assert.True(complaint.Assign(PlumberId, "Suresh", Raised.AddHours(2)).IsFailure);
    }

    [Fact]
    public void Escalation_climbs_one_rung_at_a_time()
    {
        // Stored rather than derived, so the sweeper can tell a first breach from a second
        // and escalate gradually instead of re-alerting the same people every pass.
        var complaint = Complaint();

        Assert.Equal(1, complaint.Escalate(Due.AddHours(1)));
        Assert.Equal(2, complaint.Escalate(Due.AddHours(5)));
        Assert.Equal(Due.AddHours(5), complaint.LastEscalatedAtUtc);
    }

    [Fact]
    public void Internal_notes_are_marked_as_such()
    {
        // Without the distinction, either the resident reads "third time this month, the
        // plumber is useless", or staff stop writing anything useful down.
        var complaint = Complaint();

        complaint.AddNote(PlumberId, "Third callout this month — recommend replacing the unit.",
            Raised.AddHours(4), internalOnly: true);
        complaint.AddNote(PlumberId, "Parts ordered, back tomorrow morning.",
            Raised.AddHours(5), internalOnly: false);

        Assert.Equal(1, complaint.Notes.Count(n => n.IsInternalOnly));
        Assert.Equal(1, complaint.Notes.Count(n => !n.IsInternalOnly));
    }

    [Fact]
    public void Attachments_are_bounded_in_size()
    {
        Assert.Equal(8 * 1024 * 1024, ComplaintAttachment.MaxSizeBytes);
    }
}

/// <summary>Who gets told, and how urgently, as a breach goes unanswered.</summary>
public sealed class EscalationMatrixTests
{
    [Theory]
    [InlineData(1, "assignee")]
    [InlineData(2, "society-admin")]
    [InlineData(3, "committee")]
    public void Each_rung_widens_the_audience(int level, string expected) =>
        Assert.Equal(expected, EscalationMatrix.AudienceFor(level));

    [Fact]
    public void A_repeatedly_ignored_breach_gets_a_more_urgent_lane()
    {
        // Level three means two people have already failed to act, so it is not merely a
        // wider audience — it needs faster delivery too.
        Assert.Equal("Normal", EscalationMatrix.LaneFor(1));
        Assert.Equal("Normal", EscalationMatrix.LaneFor(2));
        Assert.Equal("Gate", EscalationMatrix.LaneFor(3));
    }
}
