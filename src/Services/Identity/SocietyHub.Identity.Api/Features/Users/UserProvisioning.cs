using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Contracts.Identity;
using SocietyHub.Identity.Api.Domain;
using SocietyHub.Identity.Api.Persistence;
using SocietyHub.Persistence.Outbox;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Globalization;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Identity.Api.Features.Users;

public sealed record ProvisionUserRequest(
    string PhoneNumber,
    string FullName,
    string Role,
    Guid? FlatId,
    string? Relationship,
    string? PreferredLanguage);

public sealed record ProvisionedUserResponse(Guid UserId, Guid MembershipId, bool WasExistingUser);

public sealed class ProvisionUserValidator : AbstractValidator<ProvisionUserRequest>
{
    public ProvisionUserValidator()
    {
        RuleFor(r => r.PhoneNumber).NotEmpty().WithErrorCode("Phone.Required");

        RuleFor(r => r.FullName)
            .NotEmpty().WithErrorCode("Name.Required")
            .MaximumLength(200).WithErrorCode("Name.TooLong");

        RuleFor(r => r.Role)
            .NotEmpty().WithErrorCode("Role.Required")
            .Must(SocietyHubRoles.All.Contains).WithErrorCode("Role.Unknown");

        // A resident without a flat has nothing to be a resident of.
        RuleFor(r => r.FlatId)
            .NotNull()
            .When(r => r.Role == SocietyHubRoles.Resident)
            .WithErrorCode("Flat.RequiredForResident");
    }
}

/// <summary>
/// Adds a person to a society.
///
/// There is no self-service sign-up, deliberately. Anyone could claim to live in a building,
/// and residency is exactly what the platform's access control rests on — so a committee or
/// administrator provisions the person and the resident then signs in with an OTP to the
/// number that was registered.
/// </summary>
public static class UserProvisioningEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/users", ProvisionAsync)
           .RequireAuthorization(SocietyHubPolicies.SocietyAdministration)
           .WithValidation<ProvisionUserRequest>()
           .WithTags("Users")
           .WithSummary("Adds a person to the current society, creating the account if new.");

        return app;
    }

    private static async Task<IResult> ProvisionAsync(
        ProvisionUserRequest request,
        SocietyHubIdentityDbContext context,
        IOutbox outbox,
        ITenantContext tenant,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var societyId = tenant.RequireSocietyId();
        var now = timeProvider.GetUtcNow();

        var phoneResult = PhoneNumber.CreateNational(request.PhoneNumber, "91");

        if (phoneResult.IsFailure)
        {
            return Result.Failure(phoneResult.Error).ToProblem();
        }

        var phone = phoneResult.Value;

        // A person is global, so someone already on the platform for another society is
        // reused rather than duplicated — this is how one login spans several societies.
        var user = await context.Users
            .SingleOrDefaultAsync(u => u.PhoneNumber == phone.Value, cancellationToken);

        var wasExisting = user is not null;

        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = phone.Value,
                PhoneNumber = phone.Value,
                PhoneNumberConfirmed = false,
                FullName = request.FullName,
                PreferredLanguage = request.PreferredLanguage,
                CreatedAtUtc = now,
            };

            context.Users.Add(user);
        }

        var alreadyMember = await context.SocietyMemberships
            .IgnoreQueryFilters()
            .AnyAsync(
                m => m.UserId == user.Id && m.SocietyId == societyId && m.IsActive,
                cancellationToken);

        if (alreadyMember)
        {
            return Result
                .Failure(Error.Conflict("Membership.Exists", "This person is already a member."))
                .ToProblem();
        }

        var membership = new SocietyMembership(
            Guid.CreateVersion7(), user.Id, societyId, request.Role)
        {
            FlatId = request.FlatId,
            Relationship = request.Relationship,
        };

        context.SocietyMemberships.Add(membership);

        // Staged, not sent. The event and the rows above commit together, so the Society and
        // Notification services can never be told about a resident whose creation rolled back.
        outbox.Enqueue(new UserRegistered
        {
            SocietyId = societyId,
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            FullName = user.FullName,
            PhoneNumber = phone.Value,
            Roles = [request.Role],
        });

        await context.SaveChangesAsync(cancellationToken);

        return Microsoft.AspNetCore.Http.Results.Created(
            $"/api/users/{user.Id}",
            new ProvisionedUserResponse(user.Id, membership.Id, wasExisting));
    }
}
