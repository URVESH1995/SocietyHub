using Microsoft.AspNetCore.Http;
using SocietyHub.SharedKernel.Results;

namespace SocietyHub.Web.Results;

/// <summary>
/// Turns a domain <see cref="Result"/> into an HTTP response.
///
/// This is the only place that knows about status codes. Handlers pick an
/// <see cref="ErrorType"/> in domain terms and never mention HTTP, so the same handler can be
/// called from an endpoint, a message consumer or a background job without carrying a web
/// concern into any of them.
/// </summary>
public static class ResultExtensions
{
    public static IResult ToOk(this Result result) =>
        result.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : result.ToProblem();

    public static IResult ToOk<TValue>(this Result<TValue> result) =>
        result.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(result.Value)
            : ((Result)result).ToProblem();

    /// <summary>201 with a Location header built from the created resource.</summary>
    public static IResult ToCreated<TValue>(
        this Result<TValue> result,
        Func<TValue, string> location) =>
        result.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created(location(result.Value), result.Value)
            : ((Result)result).ToProblem();

    /// <summary>
    /// Renders the failure as RFC 9457 problem details.
    ///
    /// The stable <see cref="Error.Code"/> is surfaced as a <c>code</c> extension rather than
    /// being buried in prose, because clients must branch on it and — since the app ships in
    /// English and Hindi — must localise it. <c>detail</c> carries the English description for
    /// logs and developers, and is never what a resident sees.
    /// </summary>
    /// <summary>
    /// Renders an error directly, for the checks a handler makes before it has a
    /// <see cref="Result"/> to fail — an entity that was not found, or a caller who owns the
    /// row but not the right to change it.
    ///
    /// An overload rather than relying on the implicit <c>Error</c> to <c>Result</c>
    /// conversion, because C# does not apply user-defined conversions when resolving extension
    /// methods, so <c>error.ToProblem()</c> would not compile without it.
    /// </summary>
    public static IResult ToProblem(this Error error) => Result.Failure(error).ToProblem();

    public static IResult ToProblem(this Result result)
    {
        if (result.IsSuccess)
        {
            throw new InvalidOperationException(
                "A successful result has no problem to render.");
        }

        var error = result.Error;

        return Microsoft.AspNetCore.Http.Results.Problem(
            statusCode: StatusCodeFor(error.Type),
            title: TitleFor(error.Type),
            detail: error.Description,
            type: TypeUriFor(error.Type),
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
    }

    public static int StatusCodeFor(ErrorType errorType) => errorType switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Failure => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TitleFor(ErrorType errorType) => errorType switch
    {
        ErrorType.Validation => "Validation failed",
        ErrorType.Unauthorized => "Authentication required",
        ErrorType.Forbidden => "Not permitted",
        ErrorType.NotFound => "Not found",
        ErrorType.Conflict => "Conflict",
        _ => "An unexpected error occurred",
    };

    private static string TypeUriFor(ErrorType errorType) => errorType switch
    {
        ErrorType.Validation => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        ErrorType.Unauthorized => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        ErrorType.Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        ErrorType.NotFound => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        ErrorType.Conflict => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        _ => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
    };
}
