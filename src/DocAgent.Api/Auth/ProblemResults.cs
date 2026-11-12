namespace DocAgent.Api.Auth;

internal static class ProblemResults
{
    public static IResult BadRequest(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Bad request");

    public static IResult NotFound(string detail) =>
        Results.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");
}
