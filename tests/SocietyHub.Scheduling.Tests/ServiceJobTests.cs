using SocietyHub.Scheduling.Api.Domain;

namespace SocietyHub.Scheduling.Tests;

/// <summary>
/// The job lifecycle, and the code that proves it happened.
///
/// The completion code is the mechanism that stops a vendor being paid for work nobody did. It
/// is four digits and not a secret, and that is fine — it protects against a claim, not against
/// an attacker standing in the flat.
/// </summary>
public sealed class ServiceJobTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid DriveId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid SlotId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Resident = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly Guid Flat = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");
    private static readonly Guid Technician = Guid.Parse("ffffffff-0000-0000-0000-000000000006");
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 9, 0, 0, TimeSpan.Zero);

    private static ServiceJob NewJob() =>
        new(Guid.CreateVersion7(), SocietyId, DriveId, Guid.CreateVersion7(),
            SlotId, Resident, Flat, units: 2, Now);

    private static ServiceJob JobInProgress()
    {
        var job = NewJob();
        job.AssignTechnician(Technician, "Suresh", Now);
        job.MarkEnRoute(Now);
        job.Start(Now.AddMinutes(20));

        return job;
    }

    [Fact]
    public void A_new_job_has_a_four_digit_code()
    {
        var job = NewJob();

        Assert.Equal(4, job.CompletionCode.Length);
        Assert.True(job.CompletionCode.All(char.IsAsciiDigit));
    }

    [Fact]
    public void Codes_differ_between_jobs_created_together()
    {
        // A drive of sixty is scheduled in one loop. Codes seeded microseconds apart would be
        // a predictable set, which is a set a vendor could work out.
        var codes = Enumerable.Range(0, 200).Select(_ => NewJob().CompletionCode).ToHashSet();

        Assert.True(codes.Count > 150, $"Only {codes.Count} distinct codes in 200 jobs.");
    }

    [Fact]
    public void A_job_cannot_be_completed_without_the_residents_code()
    {
        // The entire mechanism. Without this a vendor marks sixty jobs done from a van.
        var job = JobInProgress();

        var result = job.CompleteWithCode("0000", null, null, Now.AddHours(1));

        Assert.True(result.IsFailure || job.CompletionCode == "0000");

        if (job.CompletionCode != "0000")
        {
            Assert.Equal("job.wrong_code", result.Error.Code);
            Assert.NotEqual(JobStatus.Completed, job.Status);
        }
    }

    [Fact]
    public void The_right_code_completes_the_job_and_records_the_proof()
    {
        var job = JobInProgress();

        var result = job.CompleteWithCode(
            job.CompletionCode, "blob/proof.jpg", "Gas topped up.", Now.AddHours(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal("blob/proof.jpg", job.ProofPhotoKey);
        Assert.Equal(Now.AddHours(1), job.CompletedAtUtc);
    }

    [Fact]
    public void Repeated_wrong_codes_stop_being_accepted()
    {
        // Not about brute force — four digits guessed by someone in the flat is not the threat.
        // It is about a technician whose resident has gone out, trying over and over while the
        // job sits in progress forever.
        var job = JobInProgress();
        var wrong = job.CompletionCode == "1111" ? "2222" : "1111";

        for (var i = 0; i < 5; i++)
        {
            job.CompleteWithCode(wrong, null, null, Now.AddHours(1));
        }

        var result = job.CompleteWithCode(job.CompletionCode, null, null, Now.AddHours(1));

        Assert.True(result.IsFailure);
        Assert.Equal("job.too_many_attempts", result.Error.Code);
    }

    [Fact]
    public void A_job_that_never_started_cannot_be_completed()
    {
        var job = NewJob();

        var result = job.CompleteWithCode(job.CompletionCode, null, null, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("job.not_in_progress", result.Error.Code);
    }

    [Fact]
    public void Rating_happens_after_completion_not_at_the_door()
    {
        // Asking in front of the technician produces fives. A rating only means something if
        // it is given later and privately.
        var job = JobInProgress();

        Assert.True(job.Rate(4, "Good work", Now).IsFailure);

        job.CompleteWithCode(job.CompletionCode, null, null, Now.AddHours(1));

        Assert.True(job.Rate(4, "Good work", Now.AddHours(2)).IsSuccess);
        Assert.Equal(4, job.ResidentRating);
    }

    [Fact]
    public void Rescheduling_clears_the_technician_and_the_en_route_state()
    {
        // A technician who was on their way to the old slot is not on their way any more.
        // Leaving the status would tell a resident somebody is coming when nobody is.
        var job = JobInProgress();
        var newSlot = Guid.CreateVersion7();

        var result = job.RescheduleTo(newSlot, Now.AddHours(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Scheduled, job.Status);
        Assert.Equal(newSlot, job.SlotId);
        Assert.Null(job.TechnicianId);
        Assert.Null(job.EnRouteAtUtc);
        Assert.Equal(1, job.RescheduleCount);
    }

    [Fact]
    public void Reschedules_are_counted_because_repetition_is_the_signal()
    {
        // A job moved four times means either an over-committed vendor or an unreachable
        // resident, and neither shows up in a status that only records where it is now.
        var job = NewJob();

        job.RescheduleTo(Guid.CreateVersion7(), Now);
        job.RescheduleTo(Guid.CreateVersion7(), Now);
        job.RescheduleTo(Guid.CreateVersion7(), Now);

        Assert.Equal(3, job.RescheduleCount);
    }

    [Fact]
    public void A_completed_job_cannot_be_cancelled()
    {
        // Cancelling completed work would unwind a payment for a service that was delivered.
        // The route for a bad job is a complaint, which has its own SLA.
        var job = JobInProgress();
        job.CompleteWithCode(job.CompletionCode, null, null, Now.AddHours(1));

        var result = job.Cancel("changed my mind", Now.AddHours(2));

        Assert.True(result.IsFailure);
        Assert.Equal("job.completed", result.Error.Code);
    }

    [Fact]
    public void A_no_show_and_an_unavailable_resident_are_different_outcomes()
    {
        // One is the vendor's fault and counts against their reliability; the other is not,
        // and the vendor still travelled. Collapsing them would punish a vendor for a resident
        // who went out.
        var noShow = NewJob();
        noShow.MarkNoShow(Now.AddHours(4));

        var unavailable = JobInProgress();
        unavailable.MarkResidentUnavailable("Nobody answered, called twice.", Now.AddHours(1));

        Assert.Equal(JobStatus.NoShow, noShow.Status);
        Assert.Equal(JobStatus.ResidentUnavailable, unavailable.Status);
        Assert.True(noShow.IsTerminal && unavailable.IsTerminal);
    }
}

/// <summary>
/// Slots and their capacity, which comes from the people assigned rather than a typed number.
/// </summary>
public sealed class ServiceSlotTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid DriveId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private static ServiceSlot Slot() =>
        new(Guid.CreateVersion7(), SocietyId, DriveId,
            new DateOnly(2026, 9, 12), new TimeOnly(9, 0), new TimeOnly(13, 0), Now);

    [Fact]
    public void A_slot_with_nobody_assigned_has_no_capacity()
    {
        // Derived, not typed. A slot that accepts bookings before anyone is rostered is a slot
        // that promises work nobody is going to do.
        var slot = Slot();

        Assert.Equal(0, slot.Capacity);
        Assert.False(slot.CanTake(1));
    }

    [Fact]
    public void Capacity_is_the_sum_of_what_the_technicians_can_take()
    {
        var slot = Slot();

        slot.AssignTechnician(Guid.CreateVersion7(), "Suresh", 4, Now);
        slot.AssignTechnician(Guid.CreateVersion7(), "Anil", 3, Now);

        Assert.Equal(7, slot.Capacity);
        Assert.Equal(7, slot.PlacesLeft);
    }

    [Fact]
    public void A_slot_stops_taking_bookings_when_it_is_full()
    {
        var slot = Slot();
        slot.AssignTechnician(Guid.CreateVersion7(), "Suresh", 2, Now);

        Assert.True(slot.Book(2, Now).IsSuccess);

        var overflow = slot.Book(1, Now);

        Assert.True(overflow.IsFailure);
        Assert.Equal("slot.full", overflow.Error.Code);
    }

    [Fact]
    public void Removing_a_technician_below_what_is_booked_is_refused()
    {
        // It would silently oversell. Refusing forces the rota conversation that has to happen
        // anyway, rather than producing jobs with nobody to do them.
        var slot = Slot();
        var suresh = Guid.CreateVersion7();

        slot.AssignTechnician(suresh, "Suresh", 3, Now);
        slot.Book(3, Now);

        var result = slot.RemoveTechnician(suresh, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("slot.would_oversell", result.Error.Code);
    }

    [Fact]
    public void A_double_release_cannot_drive_the_count_negative()
    {
        // A negative booked count would make the slot appear to have more capacity than it
        // has, and the extra places are ones nobody can service.
        var slot = Slot();
        slot.AssignTechnician(Guid.CreateVersion7(), "Suresh", 3, Now);
        slot.Book(1, Now);

        slot.Release(1, Now);
        slot.Release(1, Now);

        Assert.Equal(0, slot.BookedCount);
        Assert.Equal(3, slot.PlacesLeft);
    }

    [Fact]
    public void A_slot_with_bookings_cannot_be_cancelled()
    {
        var slot = Slot();
        slot.AssignTechnician(Guid.CreateVersion7(), "Suresh", 3, Now);
        slot.Book(2, Now);

        var result = slot.Cancel(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("slot.has_bookings", result.Error.Code);
    }
}
