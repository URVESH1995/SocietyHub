using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Vendor.Api.Domain;
using SocietyHub.Vendor.Api.Persistence;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Vendor.Api.Features;

public sealed record RegisterVendorRequest(
    string LegalName, string TradingName, string ContactPhone, string? ContactEmail);

public sealed record SubmitKycRequest(string GstNumber, string PanNumber);

public sealed record AddDocumentRequest(VendorDocumentKind Kind, string StorageKey);

public sealed record CoverAreaRequest(string City, string PostalCode);

public sealed record DecisionRequest(string Reason);

/// <summary>What a society sees when browsing vendors. Never the KYC fields.</summary>
public sealed record VendorSummary(
    Guid Id,
    string TradingName,
    string Status,
    decimal? AverageRating,
    int JobsCompleted,
    decimal? ReliabilityPercent,
    IReadOnlyList<string> ServiceCodes);

public sealed class RegisterVendorValidator : AbstractValidator<RegisterVendorRequest>
{
    public RegisterVendorValidator()
    {
        RuleFor(r => r.LegalName)
            .NotEmpty().WithErrorCode("Vendor.LegalNameRequired")
            .MaximumLength(300).WithErrorCode("Vendor.LegalNameTooLong");

        RuleFor(r => r.TradingName)
            .NotEmpty().WithErrorCode("Vendor.TradingNameRequired");

        RuleFor(r => r.ContactPhone)
            .NotEmpty().WithErrorCode("Vendor.PhoneRequired")
            .MinimumLength(10).WithErrorCode("Vendor.PhoneTooShort");
    }
}

public sealed class DecisionValidator : AbstractValidator<DecisionRequest>
{
    public DecisionValidator() =>
        // Six months later nobody remembers whether a vendor was suspended for a safety
        // incident or a paperwork lapse, and those have opposite answers to "bring them back".
        RuleFor(r => r.Reason)
            .NotEmpty().WithErrorCode("Vendor.ReasonRequired")
            .MinimumLength(10).WithErrorCode("Vendor.ReasonTooShort");
}

public static class VendorEndpoints
{
    public static IEndpointRouteBuilder MapVendorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vendors").WithTags("Vendors");

        // Onboarding and verification are platform operations. A society administrator must
        // not be able to verify a vendor: the whole point of KYC is that somebody independent
        // of the buyer has checked, and a committee approving its own contractor is exactly
        // the arrangement this is meant to prevent.
        group.MapPost("/", RegisterAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithValidation<RegisterVendorRequest>()
             .WithSummary("Registers a vendor. It cannot be awarded work until verified.");

        group.MapPost("/{id:guid}/kyc", SubmitKycAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithSummary("Records GSTIN and PAN, moving the vendor to review.");

        group.MapPost("/{id:guid}/documents", AddDocumentAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithSummary("Attaches a KYC document by storage key.");

        group.MapPost("/{id:guid}/verify", VerifyAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithSummary("Approves a vendor after a human has reviewed its documents.");

        group.MapPost("/{id:guid}/reject", RejectAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithValidation<DecisionRequest>()
             .WithSummary("Rejects an application, with a reason.");

        group.MapPost("/{id:guid}/suspend", SuspendAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithValidation<DecisionRequest>()
             .WithSummary("Stops new work. Jobs already in flight are untouched.");

        group.MapPost("/{id:guid}/reinstate", ReinstateAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithSummary("Returns a suspended vendor to active.");

        group.MapPost("/{id:guid}/areas", CoverAreaAsync)
             .RequireAuthorization(SocietyHubPolicies.PlatformOperations)
             .WithSummary("Adds a postal code the vendor will travel to.");

        // The read side is open to any authenticated society user, because a committee
        // deciding whether to open a drive has to be able to compare vendors.
        group.MapGet("/", BrowseAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Active vendors serving a postal code, with their track record.");

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterVendorRequest request,
        VendorDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var vendor = new Domain.Vendor(
            Guid.CreateVersion7(),
            request.LegalName,
            request.TradingName,
            request.ContactPhone,
            request.ContactEmail,
            timeProvider.GetUtcNow());

        context.Vendors.Add(vendor);

        // Created alongside the vendor rather than lazily on first job. A null projection is a
        // second code path every read has to handle, and it is the one nobody tests.
        context.VendorPerformance.Add(new VendorPerformance(Guid.CreateVersion7(), vendor.Id));

        await context.SaveChangesAsync(cancellationToken);

        return Results.Created($"/api/vendors/{vendor.Id}", new { id = vendor.Id });
    }

    private static Task<IResult> SubmitKycAsync(
        Guid id,
        SubmitKycRequest request,
        VendorDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        MutateAsync(id, context, cancellationToken, vendor =>
            vendor.SubmitKyc(request.GstNumber, request.PanNumber, timeProvider.GetUtcNow()));

    private static Task<IResult> AddDocumentAsync(
        Guid id,
        AddDocumentRequest request,
        VendorDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        MutateAsync(id, context, cancellationToken, vendor =>
            vendor.AddDocument(request.Kind, request.StorageKey, timeProvider.GetUtcNow()));

    private static Task<IResult> VerifyAsync(
        Guid id,
        VendorDbContext context,
        ICurrentUser currentUser,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        MutateAsync(id, context, cancellationToken, vendor =>
            vendor.Verify(currentUser.RequireUserId(), timeProvider.GetUtcNow()));

    private static Task<IResult> RejectAsync(
        Guid id,
        DecisionRequest request,
        VendorDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        MutateAsync(id, context, cancellationToken, vendor =>
            vendor.Reject(request.Reason, timeProvider.GetUtcNow()));

    private static Task<IResult> SuspendAsync(
        Guid id,
        DecisionRequest request,
        VendorDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        MutateAsync(id, context, cancellationToken, vendor =>
            vendor.Suspend(request.Reason, timeProvider.GetUtcNow()));

    private static Task<IResult> ReinstateAsync(
        Guid id,
        VendorDbContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        MutateAsync(id, context, cancellationToken, vendor =>
            vendor.Reinstate(timeProvider.GetUtcNow()));

    private static async Task<IResult> CoverAreaAsync(
        Guid id,
        CoverAreaRequest request,
        VendorDbContext context,
        CancellationToken cancellationToken)
    {
        var vendor = await context.Vendors
            .Include(v => v.ServiceAreas)
            .FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

        if (vendor is null)
        {
            return Error.NotFound("vendor.not_found", "No such vendor.").ToProblem();
        }

        vendor.CoverArea(request.City, request.PostalCode);
        await context.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    /// <summary>
    /// The society-facing list.
    ///
    /// Filtered to Active by construction rather than by a parameter. A committee has no use
    /// for a vendor it cannot be awarded to, and returning one invites somebody to build a
    /// screen that offers it.
    /// </summary>
    private static async Task<IResult> BrowseAsync(
        string? postalCode,
        string? serviceCode,
        VendorDbContext context,
        CancellationToken cancellationToken)
    {
        var query = context.Vendors
            .AsNoTracking()
            .Where(v => v.Status == VendorStatus.Active);

        if (!string.IsNullOrWhiteSpace(postalCode))
        {
            query = query.Where(v => v.ServiceAreas.Any(a => a.PostalCode == postalCode));
        }

        var vendors = await query
            .Select(v => new
            {
                v.Id,
                v.TradingName,
                Status = v.Status.ToString(),
                ServiceCodes = context.RateCards
                    .Where(c => c.VendorId == v.Id && c.IsPublished)
                    .Select(c => c.ServiceCode)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        // Filtered after projection because a vendor with no published rate card for the
        // requested service cannot quote for it, and offering them produces a drive that
        // cannot be priced.
        if (!string.IsNullOrWhiteSpace(serviceCode))
        {
            vendors = [.. vendors.Where(v => v.ServiceCodes.Contains(serviceCode))];
        }

        var ids = vendors.Select(v => v.Id).ToList();

        var performance = await context.VendorPerformance
            .AsNoTracking()
            .Where(p => ids.Contains(p.VendorId))
            .ToDictionaryAsync(p => p.VendorId, cancellationToken);

        return Results.Ok(vendors.Select(v =>
        {
            performance.TryGetValue(v.Id, out var record);

            return new VendorSummary(
                v.Id,
                v.TradingName,
                v.Status,
                record?.AverageRating,
                record?.JobsCompleted ?? 0,
                record?.ReliabilityPercent,
                v.ServiceCodes);
        }));
    }

    /// <summary>
    /// Loads, applies a domain operation, and saves — the shape nine of these endpoints share.
    /// </summary>
    private static async Task<IResult> MutateAsync(
        Guid id,
        VendorDbContext context,
        CancellationToken cancellationToken,
        Func<Domain.Vendor, Result> operation)
    {
        var vendor = await context.Vendors
            .Include(v => v.Documents)
            .FirstOrDefaultAsync(v => v.Id == id, cancellationToken);

        if (vendor is null)
        {
            return Error.NotFound("vendor.not_found", "No such vendor.").ToProblem();
        }

        var result = operation(vendor);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        await context.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { id = vendor.Id, status = vendor.Status.ToString() });
    }
}
