using Microsoft.AspNetCore.Http;
using SocietyHub.SharedKernel.Results;
using SocietyHub.Web.Results;

namespace SocietyHub.Platform.Tests;

/// <summary>
/// Handlers pick an <see cref="ErrorType"/> in domain terms and never mention HTTP. These
/// pin the translation, so a handler author can reason about "not found" without checking
/// what status code it becomes.
/// </summary>
public sealed class ResultMappingTests
{
    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorType.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Failure, StatusCodes.Status500InternalServerError)]
    public void Each_error_type_maps_to_its_status_code(ErrorType type, int expected) =>
        Assert.Equal(expected, ResultExtensions.StatusCodeFor(type));

    [Fact]
    public void An_unknown_error_type_falls_back_to_500_rather_than_success()
    {
        // Failing closed matters: a new ErrorType added without updating the map must not
        // become a 200 carrying an error body.
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            ResultExtensions.StatusCodeFor((ErrorType)999));
    }

    [Fact]
    public void Rendering_a_successful_result_as_a_problem_is_a_programming_error()
    {
        Assert.Throws<InvalidOperationException>(() => Result.Success().ToProblem());
    }

    [Fact]
    public void A_failure_carries_a_stable_machine_readable_code()
    {
        // The app ships in English and Hindi, so clients branch and localise on the code.
        // The description is for developers and logs, never for a resident's screen.
        var error = Error.NotFound("Visitor.PassNotFound", "No pass matches that OTP.");
        var result = Result.Failure(error);

        Assert.Equal("Visitor.PassNotFound", result.Error.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public void A_failed_result_refuses_to_yield_a_value()
    {
        Result<string> result = Error.NotFound("Flat.NotFound", "No such flat.");

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void A_value_converts_implicitly_to_a_successful_result()
    {
        Result<string> result = "A-101";

        Assert.True(result.IsSuccess);
        Assert.Equal("A-101", result.Value);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void A_successful_result_cannot_carry_an_error_and_vice_versa()
    {
        // Guards the invariant at construction, so no downstream code has to defend against a
        // result that is both successful and failed.
        Assert.Throws<InvalidOperationException>(() => Result.Failure(Error.None));
    }
}
