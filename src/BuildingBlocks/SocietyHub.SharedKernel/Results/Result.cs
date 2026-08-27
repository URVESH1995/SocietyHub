namespace SocietyHub.SharedKernel.Results;

/// <summary>
/// Outcome of an operation that can fail for an expected, domain-level reason.
/// Exceptions stay reserved for genuinely exceptional faults, so the happy path and
/// the known failure paths are both visible in the signature.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
        {
            throw new InvalidOperationException("A successful result cannot carry an error.");
        }

        if (!isSuccess && error == Error.None)
        {
            throw new InvalidOperationException("A failed result must carry an error.");
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    /// <summary>
    /// Lets a method returning <see cref="Result"/> write <c>return someError;</c>, matching
    /// what <see cref="Result{TValue}"/> already allows. Without it the two read differently
    /// for no reason, and the non-generic form collects <c>Result.Failure(...)</c> noise.
    /// </summary>
    public static implicit operator Result(Error error) => Failure(error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);
}

/// <inheritdoc cref="Result" />
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error) : base(isSuccess, error) =>
        _value = value;

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed result.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
