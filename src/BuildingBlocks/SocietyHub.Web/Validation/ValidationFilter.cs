using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SocietyHub.Web.Validation;

/// <summary>
/// Validates the request body before the handler runs.
///
/// Applied per endpoint with <c>.WithValidation&lt;TRequest&gt;()</c> rather than globally,
/// so an endpoint without a validator fails at registration instead of silently accepting
/// anything — a missing validator should be a startup error, not a runtime surprise.
/// </summary>
/// <typeparam name="TRequest">The bound request type to validate.</typeparam>
public sealed class ValidationFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
        {
            // The filter is attached to an endpoint whose signature does not carry TRequest.
            // A wiring mistake, and one that would otherwise let invalid input straight through.
            throw new InvalidOperationException(
                $"Endpoint declares validation for '{typeof(TRequest).Name}' but no argument " +
                "of that type was bound.");
        }

        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>();

        if (validator is null)
        {
            throw new InvalidOperationException(
                $"No IValidator<{typeof(TRequest).Name}> is registered.");
        }

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);

        if (result.IsValid)
        {
            return await next(context);
        }

        // Grouped by property so a client can attach each message to the right field. Every
        // failure also carries FluentValidation's stable ErrorCode, which is what a Hindi
        // client localises against — the English message is for developers.
        var errors = result.Errors
            .GroupBy(failure => failure.PropertyName)
            .ToDictionary(
                group => group.Key,
                group => group.Select(f => f.ErrorMessage).ToArray());

        var codes = result.Errors
            .Where(f => !string.IsNullOrWhiteSpace(f.ErrorCode))
            .GroupBy(f => f.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorCode).Distinct().ToArray());

        return Microsoft.AspNetCore.Http.Results.ValidationProblem(
            errors,
            detail: "One or more fields failed validation.",
            title: "Validation failed",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "Request.ValidationFailed",
                ["fieldCodes"] = codes,
            });
    }
}

public static class ValidationFilterExtensions
{
    /// <summary>
    /// Registers request validation for this endpoint. Also declares the 400 response so it
    /// appears in the OpenAPI document rather than being an undocumented surprise.
    /// </summary>
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class =>
        builder
            .AddEndpointFilter<ValidationFilter<TRequest>>()
            .ProducesValidationProblem();
}
