using BuildingBlocks.Core.Results;
using Microsoft.AspNetCore.Http;

namespace BuildingBlocks.Web;

/// <summary>
/// Turns a <see cref="Result"/> into an HTTP response.
/// </summary>
/// <remarks>
/// One mapping from <see cref="ErrorType"/> to status code, shared by every service,
/// so a "conflict" means 409 everywhere and clients can rely on it. The stable
/// <c>code</c> travels as a problem-details extension; the message is display text.
/// </remarks>
public static class ResultExtensions
{
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : Problem(result.Error);

    public static IResult ToHttpResult<T>(this Result<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);

    public static IResult ToCreatedResult<T>(this Result<T> result, string location) =>
        result.IsSuccess ? Results.Created(location, result.Value) : Problem(result.Error);

    private static IResult Problem(Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        return Results.Problem(
            title: error.Message,
            statusCode: status,
            type: $"https://disciplinary-measures/errors/{error.Code}",
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
