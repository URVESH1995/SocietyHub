using SocietyHub.SharedKernel.Primitives;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Vendor.Api.Domain;

/// <summary>
/// What a vendor charges for one service, and how the price falls as more flats join.
///
/// <para>
/// This is the whole product in one aggregate. The platform's pitch to a resident is that
/// joining with their neighbours costs less than calling the same company alone, and a rate
/// card is where that stops being a slogan. If the slabs are wrong, the pitch is a lie.
/// </para>
///
/// <para>
/// Prices are held in paise as integers. Never <c>decimal</c> rounded at the edges and never
/// <c>double</c>: a drive of 60 flats multiplies the unit price 60 times and then splits a
/// platform commission off it, and a rounding error that is invisible per flat becomes a
/// number that does not reconcile against what the payment gateway actually captured.
/// </para>
/// </summary>
public sealed class RateCard : AggregateRoot, IAuditable
{
    private readonly List<PriceSlab> _slabs = [];

    private RateCard() { }

    public RateCard(
        Guid id,
        Guid vendorId,
        string serviceCode,
        string unitLabel,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        VendorId = vendorId;
        ServiceCode = serviceCode;
        UnitLabel = unitLabel;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid VendorId { get; private set; }

    /// <summary>Matches the service catalogue, e.g. <c>ac.service.split</c>.</summary>
    public string ServiceCode { get; private set; } = string.Empty;

    /// <summary>
    /// What one unit is — "per AC unit", "per flat", "per car". Shown to residents, because
    /// "₹425" means nothing without it and the difference between per-flat and per-appliance
    /// is the difference between a fair price and a complaint.
    /// </summary>
    public string UnitLabel { get; private set; } = string.Empty;

    public bool IsPublished { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? ModifiedAtUtc { get; set; }

    public Guid? ModifiedByUserId { get; set; }

    public IReadOnlyCollection<PriceSlab> Slabs => _slabs;

    /// <summary>
    /// Adds a quantity band. Validated as a set on publish, not here — a card is built up one
    /// slab at a time and is legitimately incomplete in between.
    /// </summary>
    public Result AddSlab(int minQuantity, int? maxQuantity, long unitPricePaise)
    {
        if (minQuantity < 1)
        {
            return Error.Validation("rate.bad_min", "A slab starts at one unit or more.");
        }

        if (maxQuantity is not null && maxQuantity < minQuantity)
        {
            return Error.Validation("rate.bad_range", "A slab cannot end before it begins.");
        }

        if (unitPricePaise <= 0)
        {
            // Free work is not a discount, it is a data-entry error or an attempt to game a
            // commission that is calculated as a share of the price.
            return Error.Validation("rate.bad_price", "A unit price must be more than zero.");
        }

        _slabs.Add(new PriceSlab(
            Guid.CreateVersion7(), Id, minQuantity, maxQuantity, unitPricePaise));

        return Result.Success();
    }

    /// <summary>
    /// Makes the card usable by a drive.
    ///
    /// Everything is checked here rather than on each edit, because the invariants are about
    /// the set of slabs and a half-built card cannot satisfy them.
    /// </summary>
    public Result Publish(DateTimeOffset nowUtc)
    {
        var ordered = _slabs.OrderBy(s => s.MinQuantity).ToList();

        if (ordered.Count == 0)
        {
            return Error.Validation("rate.no_slabs", "A rate card needs at least one slab.");
        }

        if (ordered[0].MinQuantity != 1)
        {
            // Without a slab starting at one, a drive that attracts a single resident has no
            // price at all — and that is the most common outcome for a new service.
            return Error.Validation(
                "rate.no_first_slab", "The first slab must start at one unit.");
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var slab = ordered[i];
            var next = i + 1 < ordered.Count ? ordered[i + 1] : null;

            if (next is null)
            {
                // The last slab must be open-ended, or a drive that grows past it has no
                // price. Quorum is a floor, not a ceiling — nothing stops 90 flats joining.
                if (slab.MaxQuantity is not null)
                {
                    return Error.Validation(
                        "rate.not_open_ended",
                        "The last slab must have no upper limit, or a large drive has no price.");
                }

                break;
            }

            if (slab.MaxQuantity is null)
            {
                return Error.Validation(
                    "rate.open_middle", "Only the last slab may be open-ended.");
            }

            // Gaps and overlaps are the two ways a quantity resolves to no price or two
            // prices. Both are silent at entry and both surface as a wrong charge.
            if (next.MinQuantity != slab.MaxQuantity + 1)
            {
                return Error.Validation(
                    "rate.discontinuous",
                    $"Slabs must be contiguous: {slab.MaxQuantity} is followed by "
                    + $"{next.MinQuantity}.");
            }

            // The invariant the entire product rests on. A card where price rises with
            // quantity is not a bulk discount, and a resident who joined a drive to save money
            // would end up paying more than calling the vendor alone.
            if (next.UnitPricePaise > slab.UnitPricePaise)
            {
                return Error.Validation(
                    "rate.price_increases",
                    "A larger slab cannot cost more per unit than a smaller one.");
            }
        }

        IsPublished = true;
        ModifiedAtUtc = nowUtc;

        return Result.Success();
    }

    /// <summary>
    /// The unit price at a given quantity.
    ///
    /// Returns a failure rather than a fallback price when nothing matches. A silent default
    /// here would charge somebody a number nobody chose, and it would do it most often at the
    /// exact quantities the slabs were built wrong around.
    /// </summary>
    public Result<long> UnitPriceFor(int quantity)
    {
        if (quantity < 1)
        {
            return Error.Validation("rate.bad_quantity", "Quantity must be at least one.");
        }

        var slab = _slabs.FirstOrDefault(s =>
            quantity >= s.MinQuantity && (s.MaxQuantity is null || quantity <= s.MaxQuantity));

        return slab is null
            ? Error.Conflict(
                "rate.no_slab_for_quantity",
                $"This rate card has no price for {quantity} units.")
            : slab.UnitPricePaise;
    }

    /// <summary>
    /// What one participant pays, and what they saved by not going alone.
    ///
    /// The saving is computed against the first slab — the price of doing it by yourself —
    /// because that is the comparison a resident is actually making, and the number the app
    /// shows them has to be one they could verify by phoning the vendor.
    /// </summary>
    public Result<PriceQuote> QuoteFor(int quantity, int unitsPerParticipant = 1)
    {
        var unitPrice = UnitPriceFor(quantity);

        if (unitPrice.IsFailure)
        {
            return unitPrice.Error;
        }

        var soloPrice = UnitPriceFor(1);

        if (soloPrice.IsFailure)
        {
            return soloPrice.Error;
        }

        var perParticipant = unitPrice.Value * unitsPerParticipant;
        var soloPerParticipant = soloPrice.Value * unitsPerParticipant;

        return new PriceQuote(
            UnitPricePaise: unitPrice.Value,
            PerParticipantPaise: perParticipant,
            SoloPricePaise: soloPerParticipant,
            SavingPaise: soloPerParticipant - perParticipant);
    }

    /// <summary>
    /// The next threshold, so the app can say "3 more flats and everyone pays ₹75 less".
    ///
    /// This is the single most effective thing the drive screen shows. A resident who can see
    /// the discount one neighbour away is a resident who forwards the link — which is how a
    /// drive reaches quorum at all.
    /// </summary>
    public NextSlabPreview? NextSlabAfter(int quantity)
    {
        var current = _slabs.FirstOrDefault(s =>
            quantity >= s.MinQuantity && (s.MaxQuantity is null || quantity <= s.MaxQuantity));

        if (current?.MaxQuantity is null)
        {
            // Already in the open-ended slab. There is nothing further to unlock, and
            // inventing a target would be a lie that costs the platform its credibility.
            return null;
        }

        var next = _slabs
            .Where(s => s.MinQuantity > quantity)
            .OrderBy(s => s.MinQuantity)
            .FirstOrDefault();

        return next is null
            ? null
            : new NextSlabPreview(
                UnitsNeeded: next.MinQuantity - quantity,
                NewUnitPricePaise: next.UnitPricePaise,
                SavingPerUnitPaise: current.UnitPricePaise - next.UnitPricePaise);
    }
}

/// <summary>One quantity band and its price.</summary>
public sealed class PriceSlab : Entity
{
    private PriceSlab() { }

    public PriceSlab(
        Guid id, Guid rateCardId, int minQuantity, int? maxQuantity, long unitPricePaise)
        : base(id)
    {
        RateCardId = rateCardId;
        MinQuantity = minQuantity;
        MaxQuantity = maxQuantity;
        UnitPricePaise = unitPricePaise;
    }

    public Guid RateCardId { get; private set; }

    public int MinQuantity { get; private set; }

    /// <summary>Null on the last slab, which is open-ended by construction.</summary>
    public int? MaxQuantity { get; private set; }

    /// <summary>Paise, not rupees. See the aggregate's remarks on why this is an integer.</summary>
    public long UnitPricePaise { get; private set; }
}

public sealed record PriceQuote(
    long UnitPricePaise,
    long PerParticipantPaise,
    long SoloPricePaise,
    long SavingPaise)
{
    public int SavingPercent =>
        SoloPricePaise == 0 ? 0 : (int)(SavingPaise * 100 / SoloPricePaise);
}

public sealed record NextSlabPreview(
    int UnitsNeeded, long NewUnitPricePaise, long SavingPerUnitPaise);
