using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Vendor.Api.Domain;
using SocietyHub.Vendor.Api.Persistence;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Vendor.Api.Features;

public sealed record SlabRequest(int MinQuantity, int? MaxQuantity, long UnitPricePaise);

public sealed record CreateRateCardRequest(
    string ServiceCode, string UnitLabel, IReadOnlyList<SlabRequest> Slabs);

/// <summary>
/// What a drive screen needs to show a resident: the price now, what they save against going
/// alone, and how far off the next discount is.
/// </summary>
public sealed record QuoteView(
    Guid VendorId,
    string TradingName,
    string ServiceCode,
    string UnitLabel,
    long UnitPricePaise,
    long PerParticipantPaise,
    long SoloPricePaise,
    long SavingPaise,
    int SavingPercent,
    int? UnitsToNextSlab,
    long? NextSlabUnitPricePaise);

public sealed class CreateRateCardValidator : AbstractValidator<CreateRateCardRequest>
{
    public CreateRateCardValidator()
    {
        RuleFor(r => r.ServiceCode)
            .NotEmpty().WithErrorCode("Rate.ServiceCodeRequired")
            .MaximumLength(100).WithErrorCode("Rate.ServiceCodeTooLong");

        RuleFor(r => r.UnitLabel)
            .NotEmpty().WithErrorCode("Rate.UnitLabelRequired");

        RuleFor(r => r.Slabs)
            .NotEmpty().WithErrorCode("Rate.SlabsRequired");
    }
}

public static class RateCardEndpoints
{
    public static IEndpointRouteBuilder MapRateCardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rate-cards").WithTags("Rate cards");

        group.MapPost("/{vendorId:guid}", CreateAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithValidation<CreateRateCardRequest>()
             .WithSummary("Creates and publishes a rate card, validating the slab set.");

        // Quoting is open to any society user. A committee cannot decide whether a drive is
        // worth opening without seeing what it would cost, and hiding the price until after
        // they commit is how a platform loses a committee's trust once.
        group.MapGet("/quote", QuoteAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Prices a service at a given number of participants.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        Guid vendorId,
        CreateRateCardRequest request,
        VendorDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var vendor = await context.Vendors
            .FirstOrDefaultAsync(v => v.Id == vendorId, cancellationToken);

        if (vendor is null)
        {
            return Error.NotFound("vendor.not_found", "No such vendor.").ToProblem();
        }

        var now = timeProvider.GetUtcNow();

        var card = new RateCard(
            Guid.CreateVersion7(), vendorId, request.ServiceCode, request.UnitLabel, now);

        foreach (var slab in request.Slabs)
        {
            var added = card.AddSlab(slab.MinQuantity, slab.MaxQuantity, slab.UnitPricePaise);

            if (added.IsFailure)
            {
                return added.ToProblem();
            }
        }

        // Publishes immediately, because a card is only useful published and the validation
        // that matters happens here. An unpublished draft would be a second state nobody has
        // a screen for.
        var published = card.Publish(now);

        if (published.IsFailure)
        {
            return published.ToProblem();
        }

        context.RateCards.Add(card);
        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/rate-cards/{card.Id}", new { id = card.Id });
    }

    /// <summary>
    /// Prices a service across every vendor who can do it.
    ///
    /// Returns all of them rather than the cheapest, because price is not the only axis a
    /// committee weighs — a vendor two rupees dearer with a 4.8 rating and no no-shows is
    /// often the right call, and a platform that hid that would be optimising for the wrong
    /// thing on the society's behalf.
    /// </summary>
    private static async Task<IResult> QuoteAsync(
        string serviceCode,
        int participants,
        int unitsPerParticipant,
        string? postalCode,
        VendorDbContext context,
        CancellationToken cancellationToken)
    {
        if (participants < 1)
        {
            return Error.Validation(
                "quote.bad_participants", "Quote for at least one participant.").ToProblem();
        }

        var units = Math.Max(1, unitsPerParticipant);

        var cards = await context.RateCards
            .AsNoTracking()
            .Include(c => c.Slabs)
            .Where(c => c.ServiceCode == serviceCode && c.IsPublished)
            .ToListAsync(cancellationToken);

        var vendorIds = cards.Select(c => c.VendorId).ToList();

        var vendors = await context.Vendors
            .AsNoTracking()
            .Where(v => vendorIds.Contains(v.Id) && v.Status == VendorStatus.Active)
            .Select(v => new
            {
                v.Id,
                v.TradingName,
                Postcodes = v.ServiceAreas.Select(a => a.PostalCode).ToList(),
            })
            .ToDictionaryAsync(v => v.Id, cancellationToken);

        var quotes = new List<QuoteView>();

        foreach (var card in cards)
        {
            if (!vendors.TryGetValue(card.VendorId, out var vendor))
            {
                // The vendor is suspended or was never verified. Their card still exists, and
                // quoting from it would offer a society a company nobody can be awarded to.
                continue;
            }

            if (!string.IsNullOrWhiteSpace(postalCode)
                && !vendor.Postcodes.Contains(postalCode, StringComparer.OrdinalIgnoreCase))
            {
                // Filtered here rather than in SQL so the reason is visible: a vendor who will
                // not travel to this society is not a cheaper option, they are no option.
                continue;
            }

            // Total units drive the slab, not participant count. Sixty flats with two ACs each
            // is a 120-unit job, and pricing it as 60 would hand the vendor a loss they would
            // notice on the first drive.
            var totalUnits = participants * units;
            var quote = card.QuoteFor(totalUnits, units);

            if (quote.IsFailure)
            {
                continue;
            }

            var next = card.NextSlabAfter(totalUnits);

            quotes.Add(new QuoteView(
                card.VendorId,
                vendor.TradingName,
                card.ServiceCode,
                card.UnitLabel,
                quote.Value.UnitPricePaise,
                quote.Value.PerParticipantPaise,
                quote.Value.SoloPricePaise,
                quote.Value.SavingPaise,
                quote.Value.SavingPercent,
                next?.UnitsNeeded,
                next?.NewUnitPricePaise));
        }

        return Results.Ok(quotes.OrderBy(q => q.PerParticipantPaise));
    }
}
