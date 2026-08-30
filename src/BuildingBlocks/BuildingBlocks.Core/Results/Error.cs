namespace BuildingBlocks.Core.Results;

/// <summary>How a failure should be surfaced to the caller.</summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    Unauthorized = 2,
    Forbidden = 3,
    NotFound = 4,
    Conflict = 5
}

/// <summary>
/// A failure with a stable code and a human-readable message.
/// </summary>
/// <param name="Code">Stable identifier clients branch on, e.g. "user.email_taken".</param>
/// <param name="Message">Display text. May change without being a breaking change.</param>
/// <param name="Type">Drives the HTTP status the API layer returns.</param>
public sealed record Error(string Code, string Message, ErrorType Type)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}
