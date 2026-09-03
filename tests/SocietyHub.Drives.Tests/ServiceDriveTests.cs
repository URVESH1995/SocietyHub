using SocietyHub.Drives.Api.Domain;

namespace SocietyHub.Drives.Tests;

/// <summary>
/// The drive lifecycle, and the money attached to it.
///
/// Every test here is about a way a resident could end up out of pocket, charged twice, or
/// promised a service that never happens. The domain is small; the consequences of getting it
/// wrong are not.
/// </summary>
public sealed class ServiceDriveTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid VendorId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid RateCardId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Chair = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private static Guid Flat(int n) => Guid.Parse($"11111111-0000-0000-0000-{n:D12}");

    private static ServiceDrive OpenDrive(int quorum = 10, int? capacity = null)
    {
        var drive = new ServiceDrive(
            Guid.CreateVersion7(), SocietyId, "ac.service.split",
            VendorId, RateCardId, Chair, quorum, Now);

        drive.Open(Now, Now.AddDays(7), Now.AddDays(14), capacity);

        return drive;
    }

    /// <summary>Joins and immediately confirms payment, which is the normal path.</summary>
    private static DriveEnrolment Join(
        ServiceDrive drive, int flat, int units = 1, long price = 60_000)
    {
        var enrolment = drive.Enrol(
            Guid.CreateVersion7(), Flat(flat), units, price, Now).Value;

        enrolment.MarkPaid($"pay_{flat}", Now);

        return enrolment;
    }

    // --- opening --------------------------------------------------------

    [Fact]
    public void A_drive_cannot_be_serviced_before_enrolment_closes()
    {
        // The vendor needs time between knowing the final count and turning up. A service date
        // on the cut-off day means somebody is arranging technicians overnight.
        var drive = new ServiceDrive(
            Guid.CreateVersion7(), SocietyId, "x", VendorId, RateCardId, Chair, 10, Now);

        var result = drive.Open(Now, Now.AddDays(7), Now.AddDays(3), capacity: null);

        Assert.True(result.IsFailure);
        Assert.Equal("drive.service_before_cutoff", result.Error.Code);
    }

    [Fact]
    public void A_drive_that_cannot_reach_its_own_quorum_is_rejected()
    {
        // Capacity below quorum guarantees a refund, and it would take the whole cut-off
        // period to discover that.
        var drive = new ServiceDrive(
            Guid.CreateVersion7(), SocietyId, "x", VendorId, RateCardId, Chair, 20, Now);

        var result = drive.Open(Now, Now.AddDays(7), Now.AddDays(14), capacity: 15);

        Assert.True(result.IsFailure);
        Assert.Equal("drive.capacity_below_quorum", result.Error.Code);
    }

    // --- enrolment ------------------------------------------------------

    [Fact]
    public void One_flat_joins_once_however_many_residents_live_there()
    {
        // Two people in a household enrolling separately would be charged twice for one
        // service, and the vendor would arrive expecting to do the work once.
        var drive = OpenDrive();
        Join(drive, 1);

        var second = drive.Enrol(Guid.CreateVersion7(), Flat(1), 1, 60_000, Now);

        Assert.True(second.IsFailure);
        Assert.Equal("drive.already_enrolled", second.Error.Code);
    }

    [Fact]
    public void A_flat_that_withdrew_may_rejoin()
    {
        // Withdrawal frees the place. Blocking a rejoin would punish somebody for changing
        // their mind twice, and there is no reason to.
        var drive = OpenDrive();
        Join(drive, 1);
        drive.Withdraw(Flat(1), Now);

        Assert.True(drive.Enrol(Guid.CreateVersion7(), Flat(1), 1, 60_000, Now).IsSuccess);
    }

    [Fact]
    public void Enrolment_stops_at_capacity()
    {
        // A drive that oversells is a drive that disappoints people who have already paid.
        var drive = OpenDrive(quorum: 2, capacity: 3);

        Join(drive, 1);
        Join(drive, 2);
        Join(drive, 3);

        var overflow = drive.Enrol(Guid.CreateVersion7(), Flat(4), 1, 60_000, Now);

        Assert.True(overflow.IsFailure);
        Assert.Equal("drive.full", overflow.Error.Code);
    }

    [Fact]
    public void Enrolment_stops_at_the_cut_off()
    {
        var drive = OpenDrive();

        var late = drive.Enrol(
            Guid.CreateVersion7(), Flat(1), 1, 60_000, Now.AddDays(8));

        Assert.True(late.IsFailure);
        Assert.Equal("drive.closed", late.Error.Code);
    }

    [Fact]
    public void Quorum_counts_flats_but_slabs_count_units()
    {
        // A flat with three ACs is one participant and three units. Quorum is about whether the
        // trip is worth making; slabs are about what the vendor's cost scales with.
        var drive = OpenDrive(quorum: 3);

        Join(drive, 1, units: 3);
        Join(drive, 2, units: 2);

        Assert.Equal(2, drive.ActiveParticipantCount);
        Assert.Equal(5, drive.ActiveUnitCount);
        Assert.False(drive.HasReachedQuorum);
    }

    [Fact]
    public void A_withdrawn_participant_stops_counting_toward_quorum()
    {
        // Counting them would let a drive close as successful with fewer flats than the vendor
        // agreed to travel for.
        var drive = OpenDrive(quorum: 2);
        Join(drive, 1);
        Join(drive, 2);

        Assert.True(drive.HasReachedQuorum);

        drive.Withdraw(Flat(2), Now);

        Assert.False(drive.HasReachedQuorum);
        Assert.Equal(1, drive.ActiveParticipantCount);
    }

    // --- settlement -----------------------------------------------------

    [Fact]
    public void Everyone_settles_to_the_final_price_and_early_joiners_are_refunded()
    {
        // The rule that makes joining early safe. Without it the rational move is to wait and
        // see, and a drive where everyone waits never opens.
        var drive = OpenDrive(quorum: 2);

        var early = Join(drive, 1, units: 1, price: 60_000);
        var late = Join(drive, 2, units: 1, price: 50_000);

        drive.CloseWithQuorum(42_500, Now.AddDays(7));

        Assert.Equal(42_500, early.FinalUnitPricePaise);
        Assert.Equal(17_500, early.RefundDuePaise);
        Assert.Equal(7_500, late.RefundDuePaise);
    }

    [Fact]
    public void A_refund_is_never_negative_even_if_the_final_price_rose()
    {
        // The rate card's non-increasing invariant should prevent this. Clamping means a bug
        // there cannot turn into an unexpected debit on somebody's card.
        var drive = OpenDrive(quorum: 1);
        var enrolment = Join(drive, 1, units: 1, price: 40_000);

        drive.CloseWithQuorum(60_000, Now.AddDays(7));

        Assert.Equal(0, enrolment.RefundDuePaise);
    }

    [Fact]
    public void A_drive_short_of_quorum_cannot_be_closed_as_successful()
    {
        var drive = OpenDrive(quorum: 5);
        Join(drive, 1);

        var result = drive.CloseWithQuorum(50_000, Now.AddDays(7));

        Assert.True(result.IsFailure);
        Assert.Equal("drive.quorum_not_reached", result.Error.Code);
    }

    // --- compensation ---------------------------------------------------

    [Fact]
    public void Missing_quorum_puts_every_payer_in_the_refund_queue()
    {
        var drive = OpenDrive(quorum: 10);
        Join(drive, 1);
        Join(drive, 2);

        drive.CloseWithoutQuorum("Reached 2 of 10.", Now.AddDays(7));

        Assert.Equal(DriveStatus.Refunding, drive.Status);
        Assert.Equal(2, drive.OutstandingRefunds.Count);
    }

    [Fact]
    public void A_drive_nobody_paid_for_is_cancelled_outright()
    {
        // Skipping Refunding matters: a drive stuck there with no refunds to make would never
        // leave it, and would be reported as owing money forever.
        var drive = OpenDrive(quorum: 10);

        drive.CloseWithoutQuorum("Nobody joined.", Now.AddDays(7));

        Assert.Equal(DriveStatus.Cancelled, drive.Status);
        Assert.Empty(drive.OutstandingRefunds);
    }

    [Fact]
    public void Somebody_whose_payment_never_completed_is_owed_nothing()
    {
        // Collapsing "paid then withdrew" and "never paid" into one state is how a refund gets
        // issued against a charge that does not exist.
        var drive = OpenDrive(quorum: 10);
        var unpaid = drive.Enrol(Guid.CreateVersion7(), Flat(1), 1, 60_000, Now).Value;
        unpaid.MarkPaymentFailed();

        drive.CloseWithoutQuorum("Reached 0 of 10.", Now.AddDays(7));

        Assert.Empty(drive.OutstandingRefunds);
        Assert.Equal(DriveStatus.Cancelled, drive.Status);
    }

    [Fact]
    public void The_drive_stays_in_refunding_until_the_last_refund_lands()
    {
        // Tracked per participant, not as one flag. A crash after forty of sixty refunds must
        // resume at the forty-first — a boolean would either re-refund everyone or strand the
        // remainder, and both are discovered by a resident.
        var drive = OpenDrive(quorum: 10);
        var first = Join(drive, 1);
        var second = Join(drive, 2);
        var third = Join(drive, 3);

        drive.CloseWithoutQuorum("Reached 3 of 10.", Now.AddDays(7));

        drive.RecordRefund(first.Id, "rfnd_1", Now);
        Assert.Equal(DriveStatus.Refunding, drive.Status);

        drive.RecordRefund(second.Id, "rfnd_2", Now);
        Assert.Equal(DriveStatus.Refunding, drive.Status);
        Assert.Single(drive.OutstandingRefunds);

        drive.RecordRefund(third.Id, "rfnd_3", Now);

        Assert.Equal(DriveStatus.Cancelled, drive.Status);
        Assert.Empty(drive.OutstandingRefunds);
    }

    [Fact]
    public void Recording_the_same_refund_twice_does_not_reopen_anything()
    {
        // The compensation loop re-derives outstanding refunds every pass, so it will
        // legitimately ask twice after a lost response. Doing so must be inert.
        var drive = OpenDrive(quorum: 10);
        var enrolment = Join(drive, 1);

        drive.CloseWithoutQuorum("Reached 1 of 10.", Now.AddDays(7));
        drive.RecordRefund(enrolment.Id, "rfnd_1", Now);
        drive.RecordRefund(enrolment.Id, "rfnd_1", Now);

        Assert.Equal(DriveStatus.Cancelled, drive.Status);
        Assert.Equal(EnrolmentStatus.Refunded, enrolment.Status);
    }

    [Fact]
    public void A_closed_drive_refuses_further_enrolment()
    {
        var drive = OpenDrive(quorum: 1);
        Join(drive, 1);
        drive.CloseWithQuorum(60_000, Now.AddDays(7));

        var late = drive.Enrol(Guid.CreateVersion7(), Flat(2), 1, 60_000, Now.AddDays(7));

        Assert.True(late.IsFailure);
        Assert.Equal("drive.not_open", late.Error.Code);
    }
}
