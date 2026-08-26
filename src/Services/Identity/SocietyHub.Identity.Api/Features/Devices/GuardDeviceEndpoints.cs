using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Identity.Api.Features.Tokens;
using SocietyHub.Identity.Api.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Identity.Api.Features.Devices;

public sealed record RegisterDeviceRequest(string DeviceIdentifier, string DisplayName, string ShiftPin);

public sealed record StartShiftRequest(string DeviceIdentifier, string ShiftPin, Guid GuardUserId);

public sealed record DeviceResponse(Guid DeviceId, string DisplayName);

public sealed class RegisterDeviceValidator : AbstractValidator<RegisterDeviceRequest>
{
    public RegisterDeviceValidator()
    {
        RuleFor(r => r.DeviceIdentifier).NotEmpty().WithErrorCode("Device.Required");
        RuleFor(r => r.DisplayName).NotEmpty().WithErrorCode("Device.NameRequired");

        RuleFor(r => r.ShiftPin)
            .NotEmpty().WithErrorCode("Pin.Required")
            .Length(GuardDevice.PinLength).WithErrorCode("Pin.WrongLength")
            .Matches("^[0-9]+$").WithErrorCode("Pin.NotNumeric");
    }
}

public sealed class StartShiftValidator : AbstractValidator<StartShiftRequest>
{
    public StartShiftValidator()
    {
        RuleFor(r => r.DeviceIdentifier).NotEmpty().WithErrorCode("Device.Required");
        RuleFor(r => r.ShiftPin).NotEmpty().WithErrorCode("Pin.Required");
        RuleFor(r => r.GuardUserId).NotEmpty().WithErrorCode("Guard.Required");
    }
}

/// <summary>
/// Gate tablet enrolment and shift sign-in.
///
/// A guard tablet is shared, bolted to a desk, and used by whoever is on shift — so the device
/// is one identity and the guard is another. Every gate entry stays attributable to a person,
/// and a stolen tablet is revoked by disabling one row rather than rotating a credential every
/// guard in the society knows.
/// </summary>
public static class GuardDeviceEndpoints
{
    public static IEndpointRouteBuilder MapGuardDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").WithTags("Guard devices");

        group.MapPost("/", RegisterAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithValidation<RegisterDeviceRequest>()
             .WithSummary("Enrols a gate tablet and sets its shift PIN.");

        group.MapPost("/shift/start", StartShiftAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithValidation<StartShiftRequest>()
             .WithSummary("Starts a guard's shift on an enrolled device.");

        group.MapPost("/{deviceId:guid}/revoke", RevokeAsync)
             .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
             .WithSummary("Revokes a device. Used when a tablet is lost or stolen.");

        return group;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterDeviceRequest request,
        SocietyHubIdentityDbContext context,
        ITenantContext tenant,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();
        var now = timeProvider.GetUtcNow();

        var existing = await context.GuardDevices
            .SingleOrDefaultAsync(d => d.DeviceIdentifier == request.DeviceIdentifier, cancellationToken);

        if (existing is not null)
        {
            // Re-enrolment of a known tablet resets the PIN rather than creating a duplicate,
            // which is what a shift handover or a factory reset actually looks like.
            existing.SetShiftPin(request.ShiftPin, now);
            await context.SaveChangesAsync(cancellationToken);

            return Microsoft.AspNetCore.Http.Results.Ok(
                new DeviceResponse(existing.Id, existing.DisplayName));
        }

        var device = new GuardDevice(
            Guid.CreateVersion7(), societyId, request.DeviceIdentifier, request.DisplayName);

        device.SetShiftPin(request.ShiftPin, now);
        context.GuardDevices.Add(device);

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/devices/{device.Id}", new DeviceResponse(device.Id, device.DisplayName));
    }

    private static async Task<IResult> StartShiftAsync(
        StartShiftRequest request,
        SocietyHubIdentityDbContext context,
        ITokenIssuer tokens,
        ITenantContext tenant,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var device = await context.GuardDevices
            .SingleOrDefaultAsync(d => d.DeviceIdentifier == request.DeviceIdentifier, cancellationToken);

        // One message whether the device is unknown, revoked, locked out or the PIN is wrong.
        // Anything more specific tells someone holding a stolen tablet what to try next.
        var refused = Error.Unauthorized("Device.SignInFailed", "Could not start the shift.");

        if (device is null)
        {
            return Result.Failure(refused).ToProblem();
        }

        var guard = await context.Users
            .SingleOrDefaultAsync(u => u.Id == request.GuardUserId, cancellationToken);

        if (guard is null || guard.IsDisabled)
        {
            return Result.Failure(refused).ToProblem();
        }

        var started = device.TryStartShift(request.ShiftPin, request.GuardUserId, now);

        // Saved either way, so a failed attempt counts toward the lockout.
        device.LastSeenAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);

        if (!started)
        {
            return Result.Failure(refused).ToProblem();
        }

        var issued = await tokens.IssueAsync(guard, tenant.RequireSocietyId(), cancellationToken);

        return issued.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(new
            {
                accessToken = issued.Value.AccessToken,
                refreshToken = issued.Value.RefreshToken,
                expiresAtUtc = issued.Value.AccessTokenExpiresAtUtc,
                deviceId = device.Id,
                shiftStartedAtUtc = now,
            })
            : ((Result)issued).ToProblem();
    }

    private static async Task<IResult> RevokeAsync(
        Guid deviceId,
        SocietyHubIdentityDbContext context,
        CancellationToken cancellationToken)
    {
        var device = await context.GuardDevices
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);

        if (device is null)
        {
            return Result
                .Failure(Error.NotFound("Device.NotFound", "No such device."))
                .ToProblem();
        }

        device.Revoke();
        device.EndShift();

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.NoContent();
    }
}
