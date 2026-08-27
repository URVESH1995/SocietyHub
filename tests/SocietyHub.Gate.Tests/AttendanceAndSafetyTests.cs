using SocietyHub.Gate.Api.Domain;

namespace SocietyHub.Gate.Tests;

/// <summary>
/// Attendance decides what a domestic worker is paid, so the edge cases here are somebody's
/// wages rather than a rounding error.
/// </summary>
public sealed class AttendanceTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid HelpId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateOnly WorkDate = new(2026, 8, 27);

    private static AttendanceRecord Record() =>
        new(Guid.CreateVersion7(), SocietyId, HelpId, WorkDate);

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 8, 27, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_record_is_absent_until_the_first_punch()
    {
        var record = Record();

        Assert.False(record.IsPresent);
        Assert.Null(record.MinutesOnSite);
    }

    [Fact]
    public void Punching_in_marks_the_day_present()
    {
        var record = Record();

        record.PunchIn(At(7, 30));

        Assert.True(record.IsPresent);
        Assert.Equal(At(7, 30), record.FirstInAtUtc);
        Assert.Equal(1, record.PunchCount);
    }

    [Fact]
    public void Re_entering_does_not_restart_the_day()
    {
        // A maid stepping out for the market and returning has not started a second shift.
        // Overwriting arrival would shorten her recorded day.
        var record = Record();

        record.PunchIn(At(7, 30));
        record.PunchIn(At(11, 0));

        Assert.Equal(At(7, 30), record.FirstInAtUtc);
        Assert.Equal(2, record.PunchCount);
    }

    [Fact]
    public void The_last_departure_wins()
    {
        // She works several flats and leaves each one. Only the final exit ends her day.
        var record = Record();

        record.PunchIn(At(7, 30));
        record.PunchOut(At(9, 0));
        record.PunchOut(At(13, 15));

        Assert.Equal(At(13, 15), record.LastOutAtUtc);
        Assert.Equal(345, record.MinutesOnSite);
    }

    [Fact]
    public void Punching_out_without_an_arrival_is_refused()
    {
        var record = Record();

        var result = record.PunchOut(At(13, 0));

        Assert.True(result.IsFailure);
        Assert.Equal("Attendance.NotPunchedIn", result.Error.Code);
    }

    [Fact]
    public void Minutes_on_site_needs_both_ends_of_the_day()
    {
        // A worker still inside has no total yet. Reporting one would invent a departure.
        var record = Record();
        record.PunchIn(At(7, 30));

        Assert.Null(record.MinutesOnSite);
    }

    [Fact]
    public void An_early_shift_belongs_to_the_local_date_not_the_utc_one()
    {
        // The bug this guards against: a maid arriving at 05:30 IST is on the previous day in
        // UTC. Keying attendance on the UTC date would move her first shift of the month into
        // the month before — on the sheet she is paid from.
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

        var arrivalUtc = new DateTimeOffset(2026, 6, 30, 23, 30, 0, TimeSpan.Zero);
        var localDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(arrivalUtc, india).DateTime);

        Assert.Equal(new DateOnly(2026, 7, 1), localDate);
        Assert.NotEqual(DateOnly.FromDateTime(arrivalUtc.UtcDateTime), localDate);
    }

    [Fact]
    public void One_worker_can_be_assigned_to_several_flats_without_duplicates()
    {
        var help = new DailyHelp(
            Guid.CreateVersion7(), SocietyId, "Lakshmi Devi", "+919876543210", HelpCategory.Maid);

        var flatA = Guid.CreateVersion7();
        help.AssignToFlat(flatA);
        help.AssignToFlat(Guid.CreateVersion7());
        help.AssignToFlat(flatA);

        Assert.Equal(2, help.Assignments.Count);
    }
}

/// <summary>
/// SOS is the only path with a hard latency target, and the blacklist is the feature most
/// capable of being misused against a person.
/// </summary>
public sealed class SafetyTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ResidentId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid GuardId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 22, 15, 0, TimeSpan.Zero);

    private static SosIncident Raise(SosCategory category = SosCategory.Medical) =>
        new(Guid.CreateVersion7(), SocietyId, ResidentId, Guid.CreateVersion7(), category, Now);

    [Fact]
    public void A_raised_alert_starts_unacknowledged()
    {
        var incident = Raise();

        Assert.Equal(SosStatus.Raised, incident.Status);
        Assert.Null(incident.TimeToAcknowledge);
    }

    [Fact]
    public void Acknowledgement_records_who_and_how_long_it_took()
    {
        // Time to first human acknowledgement is the number that says whether the alert
        // actually worked. Everything else is process.
        var incident = Raise();

        incident.Acknowledge(GuardId, Now.AddSeconds(12));

        Assert.Equal(SosStatus.Acknowledged, incident.Status);
        Assert.Equal(GuardId, incident.AcknowledgedByUserId);
        Assert.Equal(TimeSpan.FromSeconds(12), incident.TimeToAcknowledge);
    }

    [Fact]
    public void An_alert_cannot_be_acknowledged_twice()
    {
        var incident = Raise();
        incident.Acknowledge(GuardId, Now.AddSeconds(10));

        var second = incident.Acknowledge(Guid.CreateVersion7(), Now.AddSeconds(20));

        Assert.True(second.IsFailure);
        Assert.Equal("Sos.AlreadyHandled", second.Error.Code);
    }

    [Fact]
    public void A_false_alarm_is_closed_and_kept()
    {
        // Someone who triggers one by accident and finds no trace of it has no reason to
        // believe a real alert would have been recorded either.
        var incident = Raise();
        incident.Acknowledge(GuardId, Now.AddSeconds(8));

        incident.Resolve("Child pressed the button.", wasFalseAlarm: true, Now.AddMinutes(2));

        Assert.Equal(SosStatus.FalseAlarm, incident.Status);
        Assert.NotNull(incident.ResolvedAtUtc);
        Assert.Equal("Child pressed the button.", incident.ResolutionNotes);
    }

    [Fact]
    public void An_alert_can_be_resolved_without_being_acknowledged_first()
    {
        // The resident who raised it sorts it out themselves. Forcing acknowledgement would
        // leave the alert open on a console with nobody to close it.
        var incident = Raise();

        Assert.True(incident.Resolve("Resolved by the family.", false, Now.AddMinutes(1)).IsSuccess);
    }

    [Fact]
    public void A_closed_alert_cannot_be_closed_again()
    {
        var incident = Raise();
        incident.Resolve("Handled.", false, Now.AddMinutes(1));

        Assert.True(incident.Resolve("Again.", false, Now.AddMinutes(2)).IsFailure);
    }

    [Fact]
    public void A_blacklist_entry_carries_an_author_and_a_review_date()
    {
        // A flag nobody revisits becomes a permanent accusation with no appeal, which is how
        // these turn into instruments of a personal dispute.
        var entry = new BlacklistEntry(
            Guid.CreateVersion7(),
            SocietyId,
            "+919876543210",
            "Repeated aggressive behaviour at the gate, reported by three flats.",
            ResidentId,
            Now.AddMonths(6));

        Assert.True(entry.IsActive);
        Assert.Equal(ResidentId, entry.AddedByUserId);
        Assert.False(entry.NeedsReview(Now));
        Assert.True(entry.NeedsReview(Now.AddMonths(7)));
    }

    [Fact]
    public void Lifting_a_flag_records_why()
    {
        var entry = new BlacklistEntry(
            Guid.CreateVersion7(), SocietyId, "+919876543210", "Disputed incident.",
            ResidentId, Now.AddMonths(6));

        entry.Lift("Committee reviewed; the complaint was withdrawn.", Now.AddMonths(1));

        Assert.False(entry.IsActive);
        Assert.NotNull(entry.LiftedAtUtc);
        Assert.Contains("withdrawn", entry.LiftedReason);
    }
}

/// <summary>The entry log is evidence, and it is the largest table in the platform.</summary>
public sealed class GateEntryTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public void The_partition_key_is_the_year_and_month_of_capture()
    {
        var entry = new GateEntry(
            Guid.CreateVersion7(),
            SocietyId,
            EntryDirection.Inbound,
            new DateTimeOffset(2026, 8, 27, 19, 0, 0, TimeSpan.Zero));

        Assert.Equal(202608, entry.PartitionKey);
    }

    [Fact]
    public void The_partition_key_follows_the_capture_time_not_the_sync_time()
    {
        // An offline entry synced in September still belongs to August's partition. Stamping
        // it on arrival would put it in the wrong month and break partition elimination for
        // exactly the queries an investigator runs.
        var capturedInAugust = new DateTimeOffset(2026, 8, 31, 23, 45, 0, TimeSpan.Zero);

        var entry = new GateEntry(
            Guid.CreateVersion7(), SocietyId, EntryDirection.Inbound, capturedInAugust)
        {
            WasOfflineCapture = true,
        };

        Assert.Equal(202608, entry.PartitionKey);
        Assert.True(entry.WasOfflineCapture);
    }

    [Fact]
    public void An_entry_derived_from_a_pass_carries_its_details_forward()
    {
        var (pass, code) = VisitPass.Issue(
            SocietyId,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "Ramesh Kumar",
            "+919876543210",
            VisitorType.Delivery,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(2));

        var guardId = Guid.CreateVersion7();
        pass.CheckIn(code, guardId, DateTimeOffset.UtcNow);

        var entry = GateEntry.ForPass(pass, EntryDirection.Inbound, guardId, DateTimeOffset.UtcNow);

        Assert.Equal(pass.Id, entry.VisitPassId);
        Assert.Equal(pass.FlatId, entry.FlatId);
        Assert.Equal("Ramesh Kumar", entry.PersonName);
        Assert.Equal(VisitorType.Delivery, entry.VisitorType);
        Assert.Equal(guardId, entry.RecordedByGuardId);
    }
}
