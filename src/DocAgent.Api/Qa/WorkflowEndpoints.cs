using DocAgent.Api.Auth;
using DocAgent.Api.Llm;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Api.Qa;

/// <summary>Whole-document workflows: summary (QA-6) and comparison (QA-7), by map-reduce over the document's chunks.</summary>
public static class WorkflowEndpoints
{
    public const int MaxFocusLength = 500;

    public sealed record SummaryRequest(string? Focus);

    public sealed record CompareRequest(Guid? DocumentIdA, Guid? DocumentIdB, string? Focus);

    public static void MapWorkflows(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/documents/{id:guid}/summary", SummarizeAsync).RequireAuthorization(AuthPolicies.EmailConfirmed);
        app.MapPost("/api/v1/documents/compare", CompareAsync).RequireAuthorization(AuthPolicies.EmailConfirmed);
    }

    private static async Task<IResult> SummarizeAsync(
        Guid id, SummaryRequest? request, AppDbContext db, DocumentWorkflows workflows, ILlmClientFactory llm, CancellationToken ct)
    {
        var focus = request?.Focus?.Trim();
        if (focus is { Length: > MaxFocusLength })
        {
            return ProblemResults.BadRequest($"'focus' must be at most {MaxFocusLength} characters.");
        }

        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct); // tenant filter: foreign ids are null
        if (doc is null)
        {
            return ProblemResults.NotFound("Document not found.");
        }

        if (doc.Status != DocumentStatus.Ready)
        {
            return ProblemResults.Conflict("The document is not ready yet. Wait until its status is Ready.");
        }

        return await RunAsync(llm, ct, (route, timeout, registry) => workflows.SummarizeAsync(doc, string.IsNullOrEmpty(focus) ? null : focus, registry, route, timeout, ct),
            db, focus is null ? "" : focus, [doc]);
    }

    private static async Task<IResult> CompareAsync(
        CompareRequest request, AppDbContext db, DocumentWorkflows workflows, ILlmClientFactory llm, CancellationToken ct)
    {
        if (request.DocumentIdA is not { } idA || request.DocumentIdB is not { } idB)
        {
            return ProblemResults.BadRequest("'documentIdA' and 'documentIdB' are required.");
        }

        if (idA == idB)
        {
            return ProblemResults.BadRequest("Choose two different documents to compare.");
        }

        var focus = request.Focus?.Trim();
        if (focus is { Length: > MaxFocusLength })
        {
            return ProblemResults.BadRequest($"'focus' must be at most {MaxFocusLength} characters.");
        }

        var docs = await db.Documents.AsNoTracking().Where(d => d.Id == idA || d.Id == idB).ToListAsync(ct);
        var a = docs.FirstOrDefault(d => d.Id == idA);
        var b = docs.FirstOrDefault(d => d.Id == idB);
        if (a is null || b is null)
        {
            return ProblemResults.NotFound("Document not found."); // a foreign document looks the same as a missing one
        }

        if (a.Status != DocumentStatus.Ready || b.Status != DocumentStatus.Ready)
        {
            return ProblemResults.Conflict("Both documents must be ready. Wait until their status is Ready.");
        }

        return await RunAsync(llm, ct, (route, timeout, registry) => workflows.CompareAsync(a, b, string.IsNullOrEmpty(focus) ? null : focus, registry, route, timeout, ct),
            db, focus ?? "", [a, b]);
    }

    private static async Task<IResult> RunAsync(
        ILlmClientFactory llm, CancellationToken ct, Func<LlmRoute, TimeSpan, SourceRegistry, Task<WorkflowResult>> work,
        AppDbContext db, string focus, IReadOnlyList<Document> documents)
    {
        LlmRoute route;
        try
        {
            route = await llm.GetRouteAsync(ct);
        }
        catch (LlmAccessException ex)
        {
            return LlmProblems.ToResult(ex);
        }

        var timeout = (route.Limits ?? new AgentLimitsOptions().ToLimits()).Timeout;
        var registry = new SourceRegistry();
        try
        {
            var result = await work(route, timeout, registry);
            var parsed = CitationParser.Parse(result.Text, registry.Count);
            var citations = parsed.Indexes
                .Select(i => QaService.ToCitation(i, registry[i], focus, parsed.Answer, 300))
                .ToList();
            var notices = documents
                .Where(d => !string.IsNullOrWhiteSpace(d.ExtractionNotice))
                .Select(d => $"{d.FileName}: {d.ExtractionNotice}")
                .ToList();
            var answer = parsed.Answer;
            if (result.Incomplete)
            {
                answer += "\n\n" + AgentPrompts.IncompleteNote(answer);
            }

            return Results.Ok(AskEndpoints.AskResponse.From(
                new QaAnswer(answer, false, citations, route.Info, result.Incomplete, 0, notices)));
        }
        catch (LlmAccessException ex)
        {
            return LlmProblems.ToResult(ex);
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
