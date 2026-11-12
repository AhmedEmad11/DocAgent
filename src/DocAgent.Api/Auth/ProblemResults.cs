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
}
