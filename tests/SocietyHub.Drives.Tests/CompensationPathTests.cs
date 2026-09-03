using SocietyHub.Drives.Api.Domain;

namespace SocietyHub.Drives.Tests;

/// <summary>
/// Every path by which money goes back, walked end to end.
///
/// <para>
/// `P2-21`. These are the tests the roadmap called integration tests for the saga. They run
/// against the aggregate rather than a live broker, and that is the deliberate choice: the
/// compensation logic <em>is</em> the aggregate — the worker only polls it and the consumer
/// only feeds it — so a broker in the loop would test MassTransit's delivery rather than
/// whether the right people get the right money back.
/// </para>
///
/// <para>
/// What a broker genuinely adds is delivery failure, and that is covered where it lives: by
/// re-deriving outstanding refunds on every pass, which the crash-resumption tests below
/// exercise directly.
/// </para>
/// </summary>
public sealed class CompensationPathTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid VendorId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid RateCardId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Chair = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly DateTimeOffset Opened = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CutOff = Opened.AddDays(7);

    private static Guid Flat(int n) => Guid.Parse($"11111111-0000-0000-0000-{n:D12}");

    private static ServiceDrive Drive(int quorum)
    {
        var drive = new ServiceDrive(
            Guid.CreateVersion7(), SocietyId, "ac.service.split",
            VendorId, RateCardId, Chair, quorum, Opened);

        drive.Open(Opened, CutOff, CutOff.AddDays(7), capacity: null);

        return drive;
    }

    private static DriveEnrolment Join(ServiceDrive drive, int flat, long price)
    {
        var enrolment = drive.Enrol(Guid.CreateVersion7(), Flat(flat), 1, price, Opened).Value;
        enrolment.MarkPaid($"pay_{flat}", Opened);

        return enrolment;
    }

    /// <summary>Simulates the worker and the refund consumer, without either.</summary>
    private static int DrainRefunds(ServiceDrive drive, DateTimeOffset now, int max = int.MaxValue)
    {
        var issued = 0;

        // Re-derived every iteration, exactly as the worker does. That is the property under
        // test: the list is never captured once and worked through blindly.
        while (drive.OutstandingRefunds.Count > 0 && issued < max)
        {
            var next = drive.OutstandingRefunds[0];
            drive.RecordRefund(next.Id, $"rfnd_{next.Id:N}", now);
            issued++;
        }

        return issued;
    }

    // --- path 1: quorum missed ------------------------------------------

    [Fact]
    public void Quorum_missed_refunds_everyone_and_ends_cancelled()
    {
        var drive = Drive(quorum: 10);
        Join(drive, 1, 60_000);
        Join(drive, 2, 60_000);
        Join(drive, 3, 60_000);

        drive.CloseWithoutQuorum("Reached 3 of 10.", CutOff);

        Assert.Equal(DriveStatus.Refunding, drive.Status);
        Assert.Equal(3, DrainRefunds(drive, CutOff));
        Assert.Equal(DriveStatus.Cancelled, drive.Status);
        Assert.All(drive.Enrolments, e => Assert.Equal(EnrolmentStatus.Refunded, e.Status));
    }

    [Fact]
    public void Compensation_resumes_after_a_crash_rather_than_restarting()
    {
        // The property the whole design rests on. A process killed after two of five refunds
        // must come back and issue the remaining three — not all five again, and not none.
        var drive = Drive(quorum: 10);

        for (var i = 1; i <= 5; i++)
        {
            Join(drive, i, 60_000);
        }

        drive.CloseWithoutQuorum("Reached 5 of 10.", CutOff);

        // Two go out, then the process dies.
        Assert.Equal(2, DrainRefunds(drive, CutOff, max: 2));
        Assert.Equal(3, drive.OutstandingRefunds.Count);
        Assert.Equal(DriveStatus.Refunding, drive.Status);

        // It comes back and finishes.
        Assert.Equal(3, DrainRefunds(drive, CutOff.AddMinutes(5)));

        Assert.Equal(DriveStatus.Cancelled, drive.Status);
        Assert.Equal(5, drive.Enrolments.Count(e => e.Status == EnrolmentStatus.Refunded));
    }

    [Fact]
    public void Asking_for_the_same_refund_twice_produces_one_refund()
    {
        // The worker re-derives on every pass and will ask again after a lost response. If
        // this were not inert, a slow gateway would refund a drive twice.
        var drive = Drive(quorum: 10);
        var enrolment = Join(drive, 1, 60_000);

        drive.CloseWithoutQuorum("Reached 1 of 10.", CutOff);

        drive.RecordRefund(enrolment.Id, "rfnd_x", CutOff);
        drive.RecordRefund(enrolment.Id, "rfnd_x", CutOff.AddMinutes(1));
        drive.RecordRefund(enrolment.Id, "rfnd_x", CutOff.AddMinutes(2));

        Assert.Equal(DriveStatus.Cancelled, drive.Status);
        Assert.Equal(EnrolmentStatus.Refunded, enrolment.Status);
    }

    // --- path 2: withdrawal before cut-off ------------------------------

    [Fact]
    public void Withdrawing_after_paying_puts_that_one_participant_in_the_refund_queue()
    {
        // A single refund on a drive that is otherwise healthy. It must not disturb the drive's
        // own state — the other participants are still going ahead.
        var drive = Drive(quorum: 2);
        Join(drive, 1, 60_000);
        Join(drive, 2, 60_000);
        var leaver = Join(drive, 3, 60_000);

        drive.Withdraw(Flat(3), Opened.AddDays(1));

        Assert.Equal(DriveStatus.Open, drive.Status);
        Assert.Equal(EnrolmentStatus.RefundDue, leaver.Status);
        Assert.Equal(2, drive.ActiveParticipantCount);
        Assert.True(drive.HasReachedQuorum);
    }

    [Fact]
    public void Withdrawing_without_having_paid_owes_nothing()
    {
        // Collapsing "paid then left" with "never paid" is how a refund gets issued against a
        // charge that does not exist.
        var drive = Drive(quorum: 5);
        drive.Enrol(Guid.CreateVersion7(), Flat(1), 1, 60_000, Opened);

        drive.Withdraw(Flat(1), Opened.AddHours(1));

        Assert.Empty(drive.OutstandingRefunds);
        Assert.Equal(
            EnrolmentStatus.Withdrawn,
            drive.Enrolments.Single().Status);
    }

    // --- path 3: settling to a lower price ------------------------------

    [Fact]
    public void A_successful_drive_still_owes_early_joiners_the_difference()
    {
        // The compensation path that runs on the happy route, and the one most easily
        // forgotten — a partial refund nobody triggers is money the platform has quietly kept.
        var drive = Drive(quorum: 3);

        var first = Join(drive, 1, 60_000);
        var second = Join(drive, 2, 50_000);
        var third = Join(drive, 3, 42_500);

        drive.CloseWithQuorum(42_500, CutOff);

        Assert.Equal(DriveStatus.Confirming, drive.Status);
        Assert.Equal(17_500, first.RefundDuePaise);
        Assert.Equal(7_500, second.RefundDuePaise);
        Assert.Equal(0, third.RefundDuePaise);
    }

    [Fact]
    public void Nobody_who_joined_at_the_final_price_is_owed_anything()
    {
        // Guards against a refund of zero being requested for every participant, which would
        // put sixty pointless calls through a payment gateway on every successful drive.
        var drive = Drive(quorum: 2);
        Join(drive, 1, 42_500);
        Join(drive, 2, 42_500);

        drive.CloseWithQuorum(42_500, CutOff);

        Assert.All(drive.Enrolments, e => Assert.Equal(0, e.RefundDuePaise));
    }

    [Fact]
    public void A_withdrawn_participant_is_not_settled_to_the_final_price()
    {
        // They are owed everything, not the difference. Settling them first would reduce the
        // refund to the price gap and quietly keep the rest.
        var drive = Drive(quorum: 2);
        Join(drive, 1, 60_000);
        Join(drive, 2, 60_000);
        var leaver = Join(drive, 3, 60_000);

        drive.Withdraw(Flat(3), Opened.AddDays(1));
        drive.CloseWithQuorum(42_500, CutOff);

        Assert.Equal(EnrolmentStatus.RefundDue, leaver.Status);
        Assert.Equal(60_000, leaver.AmountChargedPaise);
        Assert.Null(leaver.FinalUnitPricePaise);
    }

    // --- path 4: the drive nobody joined --------------------------------

    [Fact]
    public void An_empty_drive_cancels_without_entering_the_refund_state()
    {
        // A drive stuck in Refunding with no refunds to make would never leave it, and would
        // be reported as owing money forever.
        var drive = Drive(quorum: 10);

        drive.CloseWithoutQuorum("Nobody joined.", CutOff);

        Assert.Equal(DriveStatus.Cancelled, drive.Status);
        Assert.Empty(drive.OutstandingRefunds);
    }

    [Fact]
    public void A_drive_where_every_payment_failed_cancels_cleanly()
    {
        // Enrolments exist, but no money was ever taken. The drive must end without asking a
        // payment gateway to refund charges that were never captured.
        var drive = Drive(quorum: 5);

        for (var i = 1; i <= 3; i++)
        {
            drive.Enrol(Guid.CreateVersion7(), Flat(i), 1, 60_000, Opened)
                 .Value.MarkPaymentFailed();
        }

        drive.CloseWithoutQuorum("Reached 0 of 5.", CutOff);

        Assert.Equal(DriveStatus.Cancelled, drive.Status);
        Assert.Empty(drive.OutstandingRefunds);
    }

    // --- the invariant across every path --------------------------------

    [Fact]
    public void No_path_leaves_a_paying_participant_without_a_resolution()
    {
        // The one thing that must be true however a drive ends: every person who parted with
        // money is either getting a service or getting the money back. This is the assertion
        // that would catch a new terminal state added without a compensation path.
        foreach (var quorum in new[] { 1, 3, 10 })
        {
            var drive = Drive(quorum);

            for (var i = 1; i <= 3; i++)
            {
                Join(drive, i, 60_000);
            }

            if (drive.HasReachedQuorum)
            {
                drive.CloseWithQuorum(42_500, CutOff);
            }
            else
            {
                drive.CloseWithoutQuorum("Short.", CutOff);
                DrainRefunds(drive, CutOff);
            }

            foreach (var enrolment in drive.Enrolments)
            {
                var resolved =
                    enrolment.Status is EnrolmentStatus.Refunded
                    || (enrolment.Status == EnrolmentStatus.Paid
                        && enrolment.FinalUnitPricePaise is not null);

                Assert.True(
                    resolved,
                    $"Quorum {quorum}: an enrolment ended as {enrolment.Status} with no "
                    + "service and no refund.");
            }
        }
    }
}
