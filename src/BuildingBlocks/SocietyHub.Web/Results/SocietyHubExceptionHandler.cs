using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SocietyHub.SharedKernel.Features;
using SocietyHub.SharedKernel.Tenancy;

namespace SocietyHub.Web.Results;

/// <summary>
/// Last line of defence. Turns the exceptions that genuinely escape a handler into problem
/// details, and — more importantly — decides which of them are alerts rather than responses.
/// </summary>
public sealed class SocietyHubExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<SocietyHubExceptionHandler> _logger;

    public SocietyHubExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<SocietyHubExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title, code) = Classify(exception, httpContext);

        httpContext.Response.StatusCode = status;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                // Never the exception message. A stack trace or a SQL error leaking to a
                // resident's phone is both an information disclosure and useless to them.
                Detail = status >= StatusCodes.Status500InternalServerError
                    ? "An unexpected error occurred."
                    : title,
                Extensions = { ["code"] = code },
            },
        });
    }

    private (int Status, string Title, string Code) Classify(
        Exception exception,
        HttpContext context)
    {
        switch (exception)
        {
            case TenantIsolationViolationException isolation:
                // Never a client's fault and never a 4xx. Either a coding defect or an active
                // attempt to cross a society boundary, and both warrant waking someone up.
                _logger.LogCritical(
                    isolation,
                    "TENANT ISOLATION VIOLATION on {Path}: attempted society {Attempted}, request scoped to {Current}.",
                    context.Request.Path,
                    isolation.AttemptedSocietyId,
                    isolation.CurrentSocietyId);

                return (StatusCodes.Status500InternalServerError,
                        "An unexpected error occurred",
                        "Tenant.IsolationViolation");

            case FeatureNotEnabledException feature:
                // 402 rather than 404: the society is real and so is the feature, they simply
                // are not entitled to it, and the client should offer an upgrade rather than
                // report a broken link.
                _logger.LogInformation(
                    "Feature {Feature} not enabled for society {SocietyId}.",
                    feature.FeatureKey,
                    feature.SocietyId);

                return (StatusCodes.Status402PaymentRequired,
                        "Feature not enabled",
                        "Feature.NotEnabled");

            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                // The caller hung up. Logging this as an error would bury real failures under
                // noise from every resident who closed the app mid-request.
                _logger.LogDebug("Request to {Path} was cancelled by the client.", context.Request.Path);
                return (StatusCodesExtensions.Status499ClientClosedRequest, "Request cancelled", "Request.Cancelled");

            default:
                _logger.LogError(
                    exception,
                    "Unhandled exception on {Method} {Path}.",
                    context.Request.Method,
                    context.Request.Path);

                return (StatusCodes.Status500InternalServerError,
                        "An unexpected error occurred",
                        "Server.Unexpected");
        }
    }
}

internal static class StatusCodesExtensions
{
    /// <summary>Nginx's 499. Not in <c>StatusCodes</c>, but the accurate code for a client hang-up.</summary>
    public const int Status499ClientClosedRequest = 499;
}
