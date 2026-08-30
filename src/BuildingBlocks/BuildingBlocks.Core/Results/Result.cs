using System.Diagnostics.CodeAnalysis;

namespace BuildingBlocks.Core.Results;

/// <summary>
/// The outcome of an operation that can fail for an expected reason.
/// </summary>
/// <remarks>
/// Expected failures — a duplicate email, a request that is not the caller's to
/// answer — are returned, not thrown. Exceptions are reserved for genuine faults,
/// which keeps the exception handler free to answer 500 for everything it sees.
/// </remarks>
public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        if (isSuccess && error is not null)
        {
            throw new InvalidOperationException("A successful result cannot carry an error.");
        }

        if (!isSuccess && error is null)
        {
            throw new InvalidOperationException("A failed result must carry an error.");
        }

        IsSuccess = isSuccess;
        _error = error;
    }

    private readonly Error? _error;

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error => _error
        ?? throw new InvalidOperationException("A successful result has no error.");

    public static Result Success() => new(true, null);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => Result<TValue>.Ok(value);

    public static Result<TValue> Failure<TValue>(Error error) => Result<TValue>.Fail(error);
}

/// <summary>A <see cref="Result"/> that carries a value when it succeeds.</summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    private Result(bool isSuccess, TValue? value, Error? error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    internal static Result<TValue> Ok(TValue value) => new(true, value, null);

    internal static Result<TValue> Fail(Error error) => new(false, default, error);

    /// <summary>Allows <c>return error;</c> in a method returning <see cref="Result{TValue}"/>.</summary>
    public static implicit operator Result<TValue>(Error error) => Fail(error);

    /// <summary>Allows <c>return value;</c> in a method returning <see cref="Result{TValue}"/>.</summary>
    public static implicit operator Result<TValue>(TValue value) => Ok(value);

    /// <summary>Narrows the result so the compiler knows <see cref="Value"/> is available.</summary>
    public bool TryGetValue([NotNullWhen(true)] out TValue? value)
    {
        value = IsSuccess ? _value : default;
        return IsSuccess;
    }
}
