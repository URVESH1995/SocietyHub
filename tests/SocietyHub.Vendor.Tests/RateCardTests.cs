using SocietyHub.Vendor.Api.Domain;

namespace SocietyHub.Vendor.Tests;

/// <summary>
/// Slab pricing.
///
/// This is the product in one aggregate. The platform's pitch is that joining a drive with
/// your neighbours costs less than phoning the same company alone; if the slabs are wrong the
/// pitch is a lie, and it is a lie that shows up on somebody's bill rather than in a log.
/// </summary>
public sealed class RateCardTests
{
    private static readonly Guid VendorId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A realistic AC-service card: ₹600 alone, ₹500 at ten, ₹425 at twenty-five.</summary>
    private static RateCard Card()
    {
        var card = new RateCard(
            Guid.CreateVersion7(), VendorId, "ac.service.split", "per AC unit", Now);

        card.AddSlab(1, 9, 60_000);
        card.AddSlab(10, 24, 50_000);
        card.AddSlab(25, null, 42_500);

        return card;
    }

    // --- publishing invariants ------------------------------------------

    [Fact]
    public void A_well_formed_card_publishes()
    {
        Assert.True(Card().Publish(Now).IsSuccess);
    }

    [Fact]
    public void A_card_whose_price_rises_with_quantity_is_rejected()
    {
        // The single most important rule here. A card like this is not a bulk discount, and a
        // resident who joined a drive to save money would pay more than going alone.
        var card = new RateCard(Guid.CreateVersion7(), VendorId, "x", "unit", Now);
        card.AddSlab(1, 9, 50_000);
        card.AddSlab(10, null, 60_000);

        var result = card.Publish(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("rate.price_increases", result.Error.Code);
    }

    [Fact]
    public void A_gap_between_slabs_is_rejected()
    {
        // A quantity landing in a gap has no price at all, and it fails at the moment somebody
        // is trying to pay.
        var card = new RateCard(Guid.CreateVersion7(), VendorId, "x", "unit", Now);
        card.AddSlab(1, 9, 60_000);
        card.AddSlab(15, null, 50_000);

        var result = card.Publish(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("rate.discontinuous", result.Error.Code);
    }

    [Fact]
    public void An_overlap_between_slabs_is_rejected()
    {
        // Two prices for one quantity means the charge depends on which row the query returned
        // first, which is not a thing anyone can explain to a resident.
        var card = new RateCard(Guid.CreateVersion7(), VendorId, "x", "unit", Now);
        card.AddSlab(1, 12, 60_000);
        card.AddSlab(10, null, 50_000);

        var result = card.Publish(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("rate.discontinuous", result.Error.Code);
    }

    [Fact]
    public void A_card_that_does_not_start_at_one_is_rejected()
    {
        // The most common outcome for a new service is a drive with one interested resident.
        // A card with no price for that is a screen showing an error on its first use.
        var card = new RateCard(Guid.CreateVersion7(), VendorId, "x", "unit", Now);
        card.AddSlab(5, null, 50_000);

        var result = card.Publish(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("rate.no_first_slab", result.Error.Code);
    }

    [Fact]
    public void A_card_with_no_open_ended_slab_is_rejected()
    {
        // Quorum is a floor, not a ceiling. Nothing stops ninety flats joining, and a card
        // that stops at fifty leaves the last forty unpriced.
        var card = new RateCard(Guid.CreateVersion7(), VendorId, "x", "unit", Now);
        card.AddSlab(1, 9, 60_000);
        card.AddSlab(10, 50, 50_000);

        var result = card.Publish(Now);

        Assert.True(result.IsFailure);
        Assert.Equal("rate.not_open_ended", result.Error.Code);
    }

    [Fact]
    public void A_free_slab_is_rejected()
    {
        // Zero is a data-entry error, or an attempt to game a commission calculated as a share
        // of the price.
        var card = new RateCard(Guid.CreateVersion7(), VendorId, "x", "unit", Now);

        Assert.True(card.AddSlab(1, null, 0).IsFailure);
    }

    // --- pricing --------------------------------------------------------

    [Theory]
    [InlineData(1, 60_000)]
    [InlineData(9, 60_000)]
    [InlineData(10, 50_000)]
    [InlineData(24, 50_000)]
    [InlineData(25, 42_500)]
    [InlineData(500, 42_500)]
    public void The_price_at_each_boundary_is_the_one_intended(int quantity, long expected)
    {
        // Boundaries are where off-by-one errors live, and a price that is wrong by one flat
        // at the threshold is wrong for the whole drive.
        var card = Card();

        Assert.Equal(expected, card.UnitPriceFor(quantity).Value);
    }

    [Fact]
    public void An_impossible_quantity_fails_rather_than_defaulting()
    {
        // A silent fallback would charge somebody a number nobody chose — and would do it
        // most often at exactly the quantities a broken card was built wrong around.
        Assert.True(Card().UnitPriceFor(0).IsFailure);
    }

    [Fact]
    public void The_saving_is_measured_against_going_alone()
    {
        // The comparison a resident is actually making, and one they could verify by phoning
        // the vendor. Any other baseline is a marketing number.
        var quote = Card().QuoteFor(quantity: 30).Value;

        Assert.Equal(42_500, quote.UnitPricePaise);
        Assert.Equal(60_000, quote.SoloPricePaise);
        Assert.Equal(17_500, quote.SavingPaise);
        Assert.Equal(29, quote.SavingPercent);
    }

    [Fact]
    public void A_participant_with_several_units_pays_for_all_of_them()
    {
        // A flat with three ACs pays three times the unit price. Pricing it once is the error
        // that makes a drive look cheap and a vendor refuse the next one.
        var quote = Card().QuoteFor(quantity: 30, unitsPerParticipant: 3).Value;

        Assert.Equal(42_500 * 3, quote.PerParticipantPaise);
        Assert.Equal(60_000 * 3, quote.SoloPricePaise);
    }

    // --- the nudge ------------------------------------------------------

    [Fact]
    public void The_next_threshold_is_reported_so_the_app_can_nudge()
    {
        // The most effective thing a drive screen shows. A resident who can see the discount
        // one neighbour away is the resident who forwards the link, which is how a drive
        // reaches quorum at all.
        var next = Card().NextSlabAfter(8);

        Assert.NotNull(next);
        Assert.Equal(2, next.UnitsNeeded);
        Assert.Equal(50_000, next.NewUnitPricePaise);
        Assert.Equal(10_000, next.SavingPerUnitPaise);
    }

    [Fact]
    public void There_is_no_nudge_once_the_last_slab_is_reached()
    {
        // Inventing a target a drive can never unlock is a lie, and it is the kind that costs
        // the platform its credibility the first time somebody hits the number and nothing
        // changes.
        Assert.Null(Card().NextSlabAfter(40));
    }
}
