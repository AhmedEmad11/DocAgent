namespace DocAgent.Api.Auth;

internal static class ProblemResults
{
    public static IResult BadRequest(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Bad request");

    public static IResult NotFound(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static IResult Conflict(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

    public static IResult PayloadTooLarge(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large");

    public static IResult UnsupportedMediaType(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported file type");

    /// <summary>A Problem Details response with a machine-readable <c>code</c> extension.</summary>
    public static IResult Coded(int status, string title, string detail, string code) =>
        Results.Problem(detail, statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
