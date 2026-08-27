using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Gate.Api.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;

namespace SocietyHub.Gate.Api.Features;

/// <summary>
/// Hands out short-lived links for gate photos.
///
/// The bytes never pass through this service. A guard device uploads straight to blob storage
/// with a write-only link, and a resident reads with a separate read-only one — so a 40 KB
/// photo on every one of 105,000 daily visits never touches an API replica, and gate
/// throughput at 7pm is unaffected by photo traffic.
/// </summary>
public static class PhotoEndpoints
{
    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/photos").WithTags("Visitor photos");

        group.MapPost("/upload-ticket/{passId:guid}", UploadTicketAsync)
             .RequireAuthorization(SocietyHubPolicies.GateOperations)
             .WithSummary("Returns a short-lived, write-only link for the guard device to upload to.");

        group.MapGet("/pass/{passId:guid}", PassPhotoAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Returns a short-lived, read-only link to a pass photo.");

        group.MapGet("/entry/{entryId:guid}", EntryPhotoAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Returns a short-lived, read-only link to a gate entry photo.");

        return app;
    }

    private static IResult UploadTicketAsync(
        Guid passId,
        IVisitorPhotoService photos,
        ITenantContext tenant)
    {
        var ticket = photos.CreateUploadTicket(tenant.RequireSocietyId(), passId);

        return ticket.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(ticket.Value)
            : ((Result)ticket).ToProblem();
    }

    private static async Task<IResult> PassPhotoAsync(
        Guid passId,
        GateDbContext context,
        IVisitorPhotoService photos,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        // Read through the tenant filter, so a pass belonging to another society is simply
        // not found. The photo service checks the blob prefix independently — belt and braces,
        // because blob storage has no filter of its own.
        var blobKey = await context.VisitPasses
            .Where(p => p.Id == passId)
            .Select(p => p.PhotoBlobKey)
            .SingleOrDefaultAsync(cancellationToken);

        return LinkFor(blobKey, photos, tenant);
    }

    private static async Task<IResult> EntryPhotoAsync(
        Guid entryId,
        GateDbContext context,
        IVisitorPhotoService photos,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var blobKey = await context.GateEntries
            .Where(e => e.Id == entryId)
            .Select(e => e.PhotoBlobKey)
            .SingleOrDefaultAsync(cancellationToken);

        return LinkFor(blobKey, photos, tenant);
    }

    private static IResult LinkFor(
        string? blobKey,
        IVisitorPhotoService photos,
        ITenantContext tenant)
    {
        if (string.IsNullOrWhiteSpace(blobKey))
        {
            return Result
                .Failure(Error.NotFound("Photo.NotFound", "No photo for that entry."))
                .ToProblem();
        }

        var link = photos.CreateReadLink(blobKey, tenant.RequireSocietyId());

        return link.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(new { url = link.Value.ToString() })
            : ((Result)link).ToProblem();
    }
}
