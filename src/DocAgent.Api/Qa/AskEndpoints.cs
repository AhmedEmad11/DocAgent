using DocAgent.Api.Auth;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Qa;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Api.Qa;

public static class AskEndpoints
{
    public const int MaxQuestionLength = 2000;
    public const int MaxDocumentIds = 100;

    public sealed record AskRequest(string? Question, List<Guid>? DocumentIds);

    public sealed record CitationResponse(int Index, Guid DocumentId, string FileName, int? PageNumber, string? Section, string Quote);

    public sealed record AskResponse(
        string Answer, bool NotFound, IReadOnlyList<CitationResponse> Citations, string Mode, string Provider, string Model);

    public static void MapAsk(this IEndpointRouteBuilder app)
    {
        // Stateless (SRS QA-12, D14): nothing about the question or the answer is stored.
        app.MapPost("/api/v1/ask", AskAsync).RequireAuthorization(AuthPolicies.EmailConfirmed);
    }

    private static async Task<IResult> AskAsync(AskRequest request, AppDbContext db, QaService qa, CancellationToken ct)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrEmpty(question))
        {
            return ProblemResults.BadRequest("'question' is required.");
        }

        if (question.Length > MaxQuestionLength)
        {
            return ProblemResults.BadRequest($"'question' must be at most {MaxQuestionLength} characters.");
        }

        var requested = request.DocumentIds?.Distinct().ToList();
        if (requested is { Count: > MaxDocumentIds })
        {
            return ProblemResults.BadRequest($"'documentIds' can list at most {MaxDocumentIds} documents.");
        }

        // The tenant filter limits both queries to the caller's documents.
        if (!await db.Documents.AnyAsync(d => d.Status == DocumentStatus.Ready, ct))
        {
            return ProblemResults.Conflict("You have no documents that are ready yet. Upload a document and wait until its status is Ready.");
        }

        if (requested is { Count: > 0 })
        {
            var usable = await db.Documents
                .Where(d => d.Status == DocumentStatus.Ready && requested.Contains(d.Id))
                .Select(d => d.Id)
                .ToListAsync(ct);
            var unusable = requested.Except(usable).ToList();
            if (unusable.Count > 0)
            {
                // Another user's document is reported exactly like a missing one.
                return ProblemResults.BadRequest(
                    "These documents do not exist or are not ready: " + string.Join(", ", unusable) + ".");
            }
        }

        try
        {
            var result = await qa.AskAsync(question, requested, ct);
            return Results.Ok(new AskResponse(
                result.Answer, result.NotFound,
                result.Citations.Select(c => new CitationResponse(c.Index, c.DocumentId, c.FileName, c.PageNumber, c.Section, c.Quote)).ToList(),
                result.Route.Mode, result.Route.Provider, result.Route.Model));
        }
        catch (LlmUnavailableException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Language model unavailable");
        }
        catch (QaTimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout, title: "Timed out");
        }
    }
}
