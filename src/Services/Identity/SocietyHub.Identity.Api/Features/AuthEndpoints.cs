using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SocietyHub.Identity.Api.Features.Otp;
using SocietyHub.Identity.Api.Features.Tokens;
using SocietyHub.Identity.Api.Persistence;
using SocietyHub.SharedKernel.Abstractions;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;
using SocietyHub.Web.Security;
using SocietyHub.Web.Validation;

namespace SocietyHub.Identity.Api.Features;

// ---------------------------------------------------------------------------
// Contracts
// ---------------------------------------------------------------------------

public sealed record RequestOtpRequest(string PhoneNumber);

public sealed record VerifyOtpRequest(string PhoneNumber, string Code, Guid? SocietyId);

public sealed record RefreshRequest(string RefreshToken);

public sealed record SwitchSocietyRequest(string RefreshToken, Guid SocietyId);

/// <summary>
/// Returned when a verified phone belongs to more than one society, so the client can ask
/// which one rather than guessing.
/// </summary>
public sealed record SocietyChoiceResponse(Guid UserId, string FullName, IReadOnlyList<SocietyOption> Societies);

public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    Guid SocietyId);

// ---------------------------------------------------------------------------
// Validators
// ---------------------------------------------------------------------------

public sealed class RequestOtpValidator : AbstractValidator<RequestOtpRequest>
{
    public RequestOtpValidator() =>
        RuleFor(r => r.PhoneNumber)
            .NotEmpty().WithErrorCode("Phone.Required")
            .MinimumLength(10).WithErrorCode("Phone.TooShort")
            .MaximumLength(20).WithErrorCode("Phone.TooLong");
}

public sealed class VerifyOtpValidator : AbstractValidator<VerifyOtpRequest>
{
    public VerifyOtpValidator()
    {
        RuleFor(r => r.PhoneNumber).NotEmpty().WithErrorCode("Phone.Required");

        RuleFor(r => r.Code)
            .NotEmpty().WithErrorCode("Otp.Required")
            .Length(6).WithErrorCode("Otp.WrongLength")
            .Matches("^[0-9]+$").WithErrorCode("Otp.NotNumeric");
    }
}

public sealed class RefreshValidator : AbstractValidator<RefreshRequest>
{
    public RefreshValidator() =>
        RuleFor(r => r.RefreshToken).NotEmpty().WithErrorCode("Token.Required");
}

public sealed class SwitchSocietyValidator : AbstractValidator<SwitchSocietyRequest>
{
    public SwitchSocietyValidator()
    {
        RuleFor(r => r.RefreshToken).NotEmpty().WithErrorCode("Token.Required");
        RuleFor(r => r.SocietyId).NotEmpty().WithErrorCode("Society.Required");
    }
}

// ---------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        // Anonymous by necessity — this is how someone gets a token in the first place. The
        // deny-by-default fallback policy makes this exemption explicit rather than implied.
        group.MapPost("/otp/request", RequestOtpAsync)
             .AllowAnonymous()
             .WithValidation<RequestOtpRequest>()
             .WithSummary("Sends a one-time code to a phone number.");

        group.MapPost("/otp/verify", VerifyOtpAsync)
             .AllowAnonymous()
             .WithValidation<VerifyOtpRequest>()
             .WithSummary("Verifies a code and issues tokens, or lists societies to choose from.");

        group.MapPost("/refresh", RefreshAsync)
             .AllowAnonymous()
             .WithValidation<RefreshRequest>()
             .WithSummary("Rotates a refresh token.");

        group.MapPost("/signout", SignOutAsync)
             .AllowAnonymous()
             .WithValidation<RefreshRequest>()
             .WithSummary("Ends the session.");

        group.MapPost("/switch-society", SwitchSocietyAsync)
             .RequireAuthorization()
             .WithValidation<SwitchSocietyRequest>()
             .WithSummary("Issues tokens scoped to a different society.");

        group.MapGet("/me", MeAsync)
             .RequireAuthorization(SocietyHubPolicies.RequireSociety)
             .WithSummary("Describes the caller and the society in scope.");

        return app;
    }

    private static async Task<IResult> RequestOtpAsync(
        RequestOtpRequest request,
        IOtpService otp,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await otp.RequestAsync(
            request.PhoneNumber,
            http.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        return result.ToOk();
    }

    private static async Task<IResult> VerifyOtpAsync(
        VerifyOtpRequest request,
        IOtpService otp,
        ITokenIssuer tokens,
        SocietyHubIdentityDbContext context,
        CancellationToken cancellationToken)
    {
        var verified = await otp.VerifyAsync(request.PhoneNumber, request.Code, cancellationToken);

        if (verified.IsFailure)
        {
            return ((Result)verified).ToProblem();
        }

        var identity = verified.Value;

        // Which society to sign into is only ambiguous when there are several. Asking a
        // single-society resident to choose would be a pointless extra tap for the common case.
        var societyId = request.SocietyId
                        ?? (identity.Societies.Count == 1 ? identity.Societies[0].SocietyId : null);

        if (societyId is null)
        {
            return Microsoft.AspNetCore.Http.Results.Ok(
                new SocietyChoiceResponse(identity.UserId, identity.FullName, identity.Societies));
        }

        var user = await context.Users.SingleAsync(u => u.Id == identity.UserId, cancellationToken);
        var issued = await tokens.IssueAsync(user, societyId.Value, cancellationToken);

        return issued.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(ToResponse(issued.Value))
            : ((Result)issued).ToProblem();
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        ITokenIssuer tokens,
        CancellationToken cancellationToken)
    {
        var result = await tokens.RefreshAsync(request.RefreshToken, cancellationToken);

        return result.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(ToResponse(result.Value))
            : ((Result)result).ToProblem();
    }

    private static async Task<IResult> SignOutAsync(
        RefreshRequest request,
        ITokenIssuer tokens,
        CancellationToken cancellationToken) =>
        (await tokens.RevokeAsync(request.RefreshToken, cancellationToken)).ToOk();

    private static async Task<IResult> SwitchSocietyAsync(
        SwitchSocietyRequest request,
        ITokenIssuer tokens,
        SocietyHubIdentityDbContext context,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var user = await context.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return Result.Failure(Error.NotFound("User.NotFound", "No such user.")).ToProblem();
        }

        // The old session ends rather than running alongside the new one. Two live sessions
        // for one person in two societies is a shape nothing downstream expects, and it is how
        // a stale token ends up carrying the wrong society.
        await tokens.RevokeAsync(request.RefreshToken, cancellationToken);

        var issued = await tokens.IssueAsync(user, request.SocietyId, cancellationToken);

        return issued.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(ToResponse(issued.Value))
            : ((Result)issued).ToProblem();
    }

    private static IResult MeAsync(ICurrentUser currentUser, ITenantContext tenant) =>
        Microsoft.AspNetCore.Http.Results.Ok(new
        {
            userId = currentUser.UserId,

            // A client showing "signed in as" needs a name, and email is null for every
            // resident who registered by phone — which is nearly all of them.
            fullName = currentUser.DisplayName,
            email = currentUser.Email,
            roles = currentUser.Roles,
            societyId = tenant.SocietyId,
            isPlatformScope = tenant.IsPlatformScope,
        });

    private static TokenResponse ToResponse(TokenPair pair) =>
        new(pair.AccessToken, pair.RefreshToken, pair.AccessTokenExpiresAtUtc, pair.SocietyId);
}
