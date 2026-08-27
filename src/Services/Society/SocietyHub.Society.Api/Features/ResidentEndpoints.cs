using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Society;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Society.Api.Domain;
using SocietyHub.Society.Api.Persistence;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Society.Api.Features;

public sealed record AddResidentRequest(
    Guid UserId,
    Relationship Relationship,
    bool IsPrimaryContact,
    DirectoryVisibility? DirectoryVisibility);

public sealed record UpdateVisibilityRequest(DirectoryVisibility Visibility);

public sealed record DirectoryEntry(
    Guid ResidentId,
    Guid UserId,
    string TowerName,
    string FlatNumber,
    string Relationship,
    bool IsPrimaryContact,
    bool PhoneVisible);

public sealed class AddResidentValidator : AbstractValidator<AddResidentRequest>
{
    public AddResidentValidator()
    {
        RuleFor(r => r.UserId).NotEmpty().WithErrorCode("User.Required");
        RuleFor(r => r.Relationship).IsInEnum().WithErrorCode("Relationship.Invalid");
    }
}

/// <summary>
/// Residents and the society directory.
///
/// The directory is the part worth care. It holds names, flat numbers and phone numbers for a
/// few hundred households, and nobody opted into publishing that — they moved into a building.
/// So visibility defaults to the minimum that makes it useful, exposing a phone number is
/// opt-in, and a resident who hides themselves is still visible to the committee and guards
/// who need to reach them in an emergency.
/// </summary>
public static class ResidentEndpoints
{
    public static IEndpointRouteBuilder MapResidentEndpoints(this IEndpointRouteBuilder app)
    {
        var flats = app.MapGroup("/api/flats").WithTags("Residents");

        flats.MapPost("/{flatId:guid}/residents", AddResidentAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithValidation<AddResidentRequest>()
             .WithSummary("Adds a resident to a flat and recomputes its occupancy.");

        flats.MapDelete("/{flatId:guid}/residents/{residentId:guid}", RemoveResidentAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithSummary("Records a move-out. The row is kept as evidence.");

        var directory = app.MapGroup("/api/directory").WithTags("Directory");

        directory.MapGet("/", GetDirectoryAsync)
                 .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
                 .WithSummary("Lists residents, honouring each one's visibility choice.");

        directory.MapPut("/me/visibility", UpdateMyVisibilityAsync)
                 .RequireAuthorization(SocietyHubPolicies.ResidentAccess)
                 .WithValidation<UpdateVisibilityRequest>()
                 .WithSummary("Changes what neighbours can see about you.");

        return app;
    }

    private static async Task<IResult> AddResidentAsync(
        Guid flatId,
        AddResidentRequest request,
        SocietyDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();
        var now = timeProvider.GetUtcNow();

        var flat = await context.Flats
            .Include(f => f.Residents)
            .Include(f => f.Tower)
            .SingleOrDefaultAsync(f => f.Id == flatId, cancellationToken);

        if (flat is null)
        {
            return Result.Failure(Error.NotFound("Flat.NotFound", "No such flat.")).ToProblem();
        }

        var previousOccupancy = flat.Occupancy;

        Resident resident;

        try
        {
            resident = flat.AddResident(request.UserId, request.Relationship, now, request.IsPrimaryContact);
        }
        catch (InvalidOperationException ex)
        {
            return Result
                .Failure(Error.Conflict("Resident.AlreadyInFlat", ex.Message))
                .ToProblem();
        }

        if (request.DirectoryVisibility is { } visibility)
        {
            resident.DirectoryVisibility = visibility;
        }

        outbox.Enqueue(new ResidentRegistered
        {
            SocietyId = societyId,
            ResidentId = resident.Id,
            FlatId = flat.Id,
            UserId = request.UserId,
            Relationship = request.Relationship.ToString(),
            IsPrimaryContact = resident.IsPrimaryContact,
        });

        // Only when it actually changed. Publishing on every resident addition would have
        // consumers re-processing an unchanged fact and make the event meaningless.
        if (flat.Occupancy != previousOccupancy)
        {
            outbox.Enqueue(new FlatOccupancyChanged
            {
                SocietyId = societyId,
                FlatId = flat.Id,
                Occupancy = flat.Occupancy.ToString(),
                FlatNumber = flat.FlatNumber,
                TowerName = flat.Tower?.Name ?? string.Empty,
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/flats/{flat.Id}/residents/{resident.Id}",
            new { resident.Id, resident.IsPrimaryContact, occupancy = flat.Occupancy.ToString() });
    }

    private static async Task<IResult> RemoveResidentAsync(
        Guid flatId,
        Guid residentId,
        SocietyDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var flat = await context.Flats
            .Include(f => f.Residents)
            .Include(f => f.Tower)
            .SingleOrDefaultAsync(f => f.Id == flatId, cancellationToken);

        if (flat is null)
        {
            return Result.Failure(Error.NotFound("Flat.NotFound", "No such flat.")).ToProblem();
        }

        var previousOccupancy = flat.Occupancy;

        try
        {
            flat.RemoveResident(residentId, timeProvider.GetUtcNow());
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound("Resident.NotFound", ex.Message)).ToProblem();
        }

        if (flat.Occupancy != previousOccupancy)
        {
            outbox.Enqueue(new FlatOccupancyChanged
            {
                SocietyId = tenant.RequireSocietyId(),
                FlatId = flat.Id,
                Occupancy = flat.Occupancy.ToString(),
                FlatNumber = flat.FlatNumber,
                TowerName = flat.Tower?.Name ?? string.Empty,
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.NoContent();
    }

    private static async Task<IResult> GetDirectoryAsync(
        SocietyDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        // Committee and administrators see everyone, because they are who a resident needs to
        // reach in an emergency and hiding from them would defeat the purpose.
        var seesEveryone =
            currentUser.IsInRole(SocietyHubRoles.CommitteeMember)
            || currentUser.IsInRole(SocietyHubRoles.SocietyAdmin)
            || currentUser.IsInRole(SocietyHubRoles.SuperAdmin);

        var query = context.Residents
            .AsNoTracking()
            .Include(r => r.Flat)
            .ThenInclude(f => f!.Tower)
            .Where(r => r.MovedOutAtUtc == null);

        if (!seesEveryone)
        {
            query = query.Where(r => r.DirectoryVisibility != DirectoryVisibility.Hidden);
        }

        var residents = await query.ToListAsync(cancellationToken);

        var entries = residents.Select(r => new DirectoryEntry(
            r.Id,
            r.UserId,
            r.Flat?.Tower?.Name ?? string.Empty,
            r.Flat?.FlatNumber ?? string.Empty,
            r.Relationship.ToString(),
            r.IsPrimaryContact,

            // The phone number itself is not held here — Identity owns it. This flag tells the
            // client whether it may ask for it, which keeps the contact detail in one service
            // rather than copied into a directory that then goes stale.
            PhoneVisible: seesEveryone || r.DirectoryVisibility == DirectoryVisibility.NameFlatAndPhone));

        return Microsoft.AspNetCore.Http.Results.Ok(entries);
    }

    private static async Task<IResult> UpdateMyVisibilityAsync(
        UpdateVisibilityRequest request,
        SocietyDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();

        // Their own records only. Without this filter a resident could rewrite a neighbour's
        // privacy choice, and the tenant filter alone would not stop it.
        var mine = await context.Residents
            .Where(r => r.UserId == userId && r.MovedOutAtUtc == null)
            .ToListAsync(cancellationToken);

        if (mine.Count == 0)
        {
            return Result
                .Failure(Error.NotFound("Resident.NotFound", "You are not listed in this society."))
                .ToProblem();
        }

        foreach (var resident in mine)
        {
            resident.DirectoryVisibility = request.Visibility;
        }

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.NoContent();
    }
}
