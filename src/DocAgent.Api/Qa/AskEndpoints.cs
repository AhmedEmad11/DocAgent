using System.Text.Json;
using System.Threading.Channels;
using DocAgent.Api.Auth;
using DocAgent.Api.Llm;
using DocAgent.Api.Security;
using Microsoft.AspNetCore.RateLimiting;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Api.Qa;

public static class AskEndpoints
{
    public const int MaxQuestionLength = 2000;
    public const int MaxDocumentIds = 100;

    public sealed record AskRequest(string? Question, List<Guid>? DocumentIds);

    public sealed record CitationResponse(int Index, Guid DocumentId, string FileName, int? PageNumber, string? Section, string Quote);

    /// <summary>
    /// Shared by /ask, /ask/stream, the summary and the comparison. <c>Incomplete</c>: a limit was reached and the answer may
    /// be incomplete (QA-11). <c>ToolCalls</c>: tool calls the agent made. <c>Notices</c>: extraction notices (DOC-11) of the cited documents; demo answers start with the privacy notice (PRIV-1).
    /// </summary>
    public sealed record AskResponse(
        string Answer, bool NotFound, IReadOnlyList<CitationResponse> Citations, string Mode, string Provider, string Model,
        bool Incomplete, int ToolCalls, IReadOnlyList<string> Notices)
    {
        public static AskResponse From(QaAnswer result) => new(
            result.Answer, result.NotFound,
            result.Citations.Select(c => new CitationResponse(c.Index, c.DocumentId, c.FileName, c.PageNumber, c.Section, c.Quote)).ToList(),
            result.Route.Mode, result.Route.Provider, result.Route.Model,
            result.Incomplete, result.ToolCalls, WithPrivacyNotice(result.Route.Mode, result.Notices));

        /// <summary>PRIV-1: demo answers always carry the third-party notice, first.</summary>
        private static IReadOnlyList<string> WithPrivacyNotice(string mode, IReadOnlyList<string>? notices) =>
            mode == "demo" ? [DemoNoKeyRouteProvider.PrivacyNotice, .. notices ?? []] : notices ?? [];
    }

    public static void MapAsk(this IEndpointRouteBuilder app)
    {
        // Stateless (SRS QA-12, D14): nothing about the question or the answer is stored.
        app.MapPost("/api/v1/ask", AskAsync)
            .RequireAuthorization(AuthPolicies.EmailConfirmed)
            .RequireRateLimiting(ResourceRateLimiting.AskPolicy)
            .WithTags("Questions")
            .Produces<AskResponse>()
            .WithAnswerProblems()
            .WithSummary("Ask a question about my documents")
            .WithDescription(
                "Answers from the caller's Ready documents (all of them, or the ones listed in documentIds) with citations and a verification pass; "
                + "'notFound' is true when the documents do not contain the answer. 'mode' says whether the demo model or the caller's own key served it. "
                + "Stateless: nothing about the question or answer is stored. question is at most 2000 characters, documentIds at most 100. "
                + "Errors carry a 'code': demo_quota_exceeded and demo_rate_limited (demo), llm_key_required, llm_key_invalid, llm_no_credit, llm_rate_limited, llm_unavailable, llm_model_unknown, llm_request_rejected, llm_key_unreadable. "
                + "At most 4 questions per user at a time (429).");

        app.MapPost("/api/v1/ask/stream", AskStreamAsync)
            .RequireAuthorization(AuthPolicies.EmailConfirmed)
            .RequireRateLimiting(ResourceRateLimiting.AskPolicy)
            .WithTags("Questions")
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream")
            .WithAnswerProblems()
            .WithSummary("Ask a question and stream progress (Server-Sent Events)")
            .WithDescription(
                "Same request and validation as POST /api/v1/ask; those errors are normal responses before the stream starts. "
                + "Events: searching, tool_call (data: {\"tool\": name}), verifying, then answer (data: the /ask response JSON) or error (data: status, title, optional code). "
                + "The answer arrives whole after verification, not token by token.");
    }

    /// <summary>Problem responses shared by every endpoint that runs the language model.</summary>
    internal static RouteHandlerBuilder WithAnswerProblems(this RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status402PaymentRequired)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
        .ProducesProblem(StatusCodes.Status504GatewayTimeout);

    private static async Task<IResult> AskAsync(AskRequest request, AppDbContext db, AskService ask, CancellationToken ct)
    {
        var (question, requested, error) = await ValidateAsync(request, db, ct);
        if (error is not null)
        {
            return error;
        }

        try
        {
            return Results.Ok(AskResponse.From(await ask.AskAsync(question!, requested, ct)));
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

    /// <summary>
    /// Server-Sent Events (QA-10). Same validation and status codes as /ask (they are returned as normal responses before
    /// the stream starts). Events: <c>searching</c>, <c>tool_call</c> (data: the tool name only), <c>verifying</c>, then
    /// <c>answer</c> (data: the /ask JSON) or <c>error</c> (data: status and title). The answer itself arrives whole, after verification.
    /// </summary>
    private static async Task<IResult> AskStreamAsync(
        AskRequest request, HttpContext http, AppDbContext db, AskService ask, ILlmClientFactory llm, CancellationToken ct)
    {
        var (question, requested, error) = await ValidateAsync(request, db, ct);
        if (error is not null)
        {
            return error;
        }

        try
        {
            await llm.GetRouteAsync(ct); // the route is cached for this request; a missing or unreadable key is a normal 4xx, not a stream error
        }
        catch (LlmAccessException ex)
        {
            return LlmProblems.ToResult(ex);
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var events = Channel.CreateUnbounded<(string Name, string Data)>();
        var work = Task.Run(async () =>
        {
            try
            {
                var result = await ask.AskAsync(question!, requested, ct, e =>
                    events.Writer.TryWrite((e.Type, e.Tool is null ? "{}" : JsonSerializer.Serialize(new { tool = e.Tool }))));
                events.Writer.TryWrite(("answer", JsonSerializer.Serialize(AskResponse.From(result), JsonSerializerOptions.Web)));
            }
            catch (LlmAccessException ex)
            {
                var m = LlmProblems.Map(ex, onSave: false);
                events.Writer.TryWrite(("error", JsonSerializer.Serialize(new { status = m.Status, title = m.Title, code = m.Code, retryAfter = m.RetryAfter is { } r ? (int?)Math.Ceiling(r.TotalSeconds) : null })));
            }
            catch (LlmUnavailableException)
            {
                events.Writer.TryWrite(("error", JsonSerializer.Serialize(new { status = 503, title = "Language model unavailable" })));
            }
            catch (QaTimeoutException)
            {
                events.Writer.TryWrite(("error", JsonSerializer.Serialize(new { status = 504, title = "Timed out" })));
            }
            catch (OperationCanceledException)
            {
                // the client went away
            }
            catch (Exception)
            {
                events.Writer.TryWrite(("error", JsonSerializer.Serialize(new { status = 500, title = "The question could not be answered" })));
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var (name, data) in events.Reader.ReadAllAsync(ct))
            {
                await http.Response.WriteAsync($"event: {name}\ndata: {data}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // client disconnected; the work task sees the same token
        }

        await work;
        return Results.Empty;
    }

    private static async Task<(string? Question, List<Guid>? Requested, IResult? Error)> ValidateAsync(
        AskRequest request, AppDbContext db, CancellationToken ct)
    {
        var question = request.Question?.Trim();
        if (string.IsNullOrEmpty(question))
        {
            return (null, null, ProblemResults.BadRequest("'question' is required."));
        }

        if (question.Length > MaxQuestionLength)
        {
            return (null, null, ProblemResults.BadRequest($"'question' must be at most {MaxQuestionLength} characters."));
        }

        var requested = request.DocumentIds?.Distinct().ToList();
        if (requested is { Count: > MaxDocumentIds })
        {
            return (null, null, ProblemResults.BadRequest($"'documentIds' can list at most {MaxDocumentIds} documents."));
        }

        // The tenant filter limits both queries to the caller's documents.
        if (!await db.Documents.AnyAsync(d => d.Status == DocumentStatus.Ready, ct))
        {
            return (null, null, ProblemResults.Conflict("You have no documents that are ready yet. Upload a document and wait until its status is Ready."));
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
                return (null, null, ProblemResults.BadRequest(
                    "These documents do not exist or are not ready: " + string.Join(", ", unusable) + "."));
            }
        }

        return (question, requested, null);
    }
}
