using SocietyHub.Payments.Api.Domain;

namespace SocietyHub.Payments.Tests;

/// <summary>
/// The money.
///
/// Every test here is a way somebody could be charged twice, refunded money they never paid, or
/// have a service delivered they never paid for. The domain is deliberately dull; these are
/// what make it safe.
/// </summary>
public sealed class PaymentOrderTests
{
    private static readonly Guid SocietyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid EnrolmentId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private const long Amount = 120_000;

    private static PaymentOrder NewOrder() =>
        new(Guid.CreateVersion7(), SocietyId, UserId, "drive_enrolment", EnrolmentId, Amount, Now);

    private static PaymentOrder PaidOrder()
    {
        var order = NewOrder();
        order.MarkPaid("pay_abc123", Amount, Now);

        return order;
    }

    // --- capture --------------------------------------------------------

    [Fact]
    public void Capturing_records_a_ledger_entry()
    {
        var order = PaidOrder();

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Single(order.Ledger);
        Assert.Equal(Amount, order.Ledger.Single().AmountPaise);
    }

    [Fact]
    public void The_same_capture_arriving_twice_is_inert()
    {
        // Reached from both the client callback and the webhook, deliberately, because either
        // can be lost. Whichever arrives second must do nothing, or the ledger double-counts
        // and every reconciliation after it is wrong.
        var order = PaidOrder();

        var again = order.MarkPaid("pay_abc123", Amount, Now.AddSeconds(5));

        Assert.True(again.IsSuccess);
        Assert.Single(order.Ledger);
        Assert.Equal(Amount, order.NetPaise);
    }

    [Fact]
    public void A_second_different_payment_against_one_order_is_refused()
    {
        // Two real captures on one order means somebody paid twice. It must surface rather
        // than be reconciled away later.
        var order = PaidOrder();

        var result = order.MarkPaid("pay_different", Amount, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("payment.already_paid", result.Error.Code);
    }

    [Fact]
    public void A_capture_for_the_wrong_amount_is_refused()
    {
        // Never a rounding difference — the amount was fixed when the order was created. It
        // means tampering, or a webhook belonging to a different order.
        var order = NewOrder();

        var result = order.MarkPaid("pay_abc123", Amount - 1, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("payment.amount_mismatch", result.Error.Code);
    }

    [Fact]
    public void A_failure_notice_after_a_capture_does_not_unwind_it()
    {
        // A late or duplicated webhook. Acting on it would mark a paid order failed and hand
        // somebody a free service.
        var order = PaidOrder();

        var result = order.MarkFailed("Card declined", Now.AddMinutes(1));

        Assert.True(result.IsFailure);
        Assert.Equal(OrderStatus.Paid, order.Status);
    }

    // --- refunds --------------------------------------------------------

    [Fact]
    public void A_full_refund_leaves_nothing_held()
    {
        var order = PaidOrder();

        order.RecordRefund("rfnd_1", Amount, "quorum_missed", Now.AddDays(7));

        Assert.Equal(OrderStatus.Refunded, order.Status);
        Assert.Equal(0, order.NetPaise);
        Assert.Equal(0, order.RefundablePaise);
    }

    [Fact]
    public void A_partial_refund_is_the_difference_an_early_joiner_is_owed()
    {
        // The drive settled cheaper than this resident paid. They keep the service and get the
        // difference, which is what makes joining early safe.
        var order = PaidOrder();

        order.RecordRefund("rfnd_1", 30_000, "price_settled", Now.AddDays(7));

        Assert.Equal(OrderStatus.PartiallyRefunded, order.Status);
        Assert.Equal(90_000, order.NetPaise);
        Assert.Equal(90_000, order.RefundablePaise);
    }

    [Fact]
    public void The_same_refund_arriving_twice_is_inert()
    {
        // The compensation loop re-derives outstanding refunds every pass and will ask again
        // after a lost response. This is the property that makes that safe.
        var order = PaidOrder();

        order.RecordRefund("rfnd_1", Amount, "quorum_missed", Now);
        order.RecordRefund("rfnd_1", Amount, "quorum_missed", Now);

        Assert.Equal(Amount, order.RefundedPaise);
        Assert.Single(order.Ledger.Where(e => e.Kind == LedgerEntryKind.Refund));
    }

    [Fact]
    public void Refunding_more_than_was_captured_is_refused()
    {
        // The one error here that cannot be corrected by another refund — it is money leaving
        // that never arrived. Refused rather than clamped, so it cannot happen quietly.
        var order = PaidOrder();
        order.RecordRefund("rfnd_1", 100_000, "price_settled", Now);

        var result = order.RecordRefund("rfnd_2", 50_000, "quorum_missed", Now);

        Assert.True(result.IsFailure);
        Assert.Equal("payment.over_refund", result.Error.Code);
        Assert.Equal(100_000, order.RefundedPaise);
    }

    [Fact]
    public void An_order_that_was_never_captured_cannot_be_refunded()
    {
        var order = NewOrder();

        var result = order.RecordRefund("rfnd_1", Amount, "quorum_missed", Now);

        Assert.True(result.IsFailure);
        Assert.Equal("payment.not_paid", result.Error.Code);
    }

    [Fact]
    public void Two_partial_refunds_that_together_cover_it_close_the_order()
    {
        // Real: an early joiner is refunded the price difference when the drive settles, and
        // then the drive is cancelled for a separate reason and the rest goes back.
        var order = PaidOrder();

        order.RecordRefund("rfnd_1", 20_000, "price_settled", Now);
        order.RecordRefund("rfnd_2", 100_000, "quorum_missed", Now.AddDays(1));

        Assert.Equal(OrderStatus.Refunded, order.Status);
        Assert.Equal(0, order.NetPaise);
    }

    // --- the ledger -----------------------------------------------------

    [Fact]
    public void The_signed_ledger_sums_to_what_is_held()
    {
        // Reconciliation is a SUM rather than a procedure, which is the whole reason entries
        // are signed. A procedure is something somebody eventually writes incorrectly.
        var order = PaidOrder();
        order.RecordRefund("rfnd_1", 45_000, "price_settled", Now);

        Assert.Equal(order.NetPaise, order.Ledger.Sum(e => e.AmountPaise));
        Assert.Equal(75_000, order.Ledger.Sum(e => e.AmountPaise));
    }

    [Fact]
    public void Refund_entries_are_negative_and_captures_positive()
    {
        var order = PaidOrder();
        order.RecordRefund("rfnd_1", 45_000, "price_settled", Now);

        Assert.True(order.Ledger.Single(e => e.Kind == LedgerEntryKind.Capture).AmountPaise > 0);
        Assert.True(order.Ledger.Single(e => e.Kind == LedgerEntryKind.Refund).AmountPaise < 0);
    }
}
