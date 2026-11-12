using DocAgent.Api.Auth;
using DocAgent.Infrastructure.Llm;

namespace DocAgent.Api.Llm;

/// <summary>
/// Maps LLM route and provider failures to Problem Details (SRS KEY-7). Every text is fixed in the code: nothing from the
/// provider's response or the key is ever copied into a response (SEC-2). The <c>code</c> extension is stable for clients.
/// </summary>
public static class LlmProblems
{
    public sealed record Mapped(int Status, string Title, string Code, string Detail, TimeSpan? RetryAfter);

    /// <param name="onSave">True while saving a key (a bad key or model is the request's fault: 400); false while using a saved key (422).</param>
    public static Mapped Map(LlmAccessException ex, bool onSave)
    {
        var rejectedStatus = onSave ? StatusCodes.Status400BadRequest : StatusCodes.Status422UnprocessableEntity;
        return ex.Kind switch
        {
            LlmFailureKind.InvalidKey => new(rejectedStatus, "LLM key rejected", "llm_key_invalid", ex.Message, null),
            LlmFailureKind.NoCredit => new(StatusCodes.Status402PaymentRequired, "No credit with the LLM provider", "llm_no_credit", ex.Message, null),
            LlmFailureKind.RateLimited => new(StatusCodes.Status429TooManyRequests, "LLM provider rate limit", "llm_rate_limited", ex.Message, ex.RetryAfter),
            LlmFailureKind.Unavailable => new(StatusCodes.Status503ServiceUnavailable, "LLM provider unavailable", "llm_unavailable", ex.Message, null),
            LlmFailureKind.UnknownModel => new(rejectedStatus, "Unknown model", "llm_model_unknown", ex.Message, null),
            LlmFailureKind.KeyRequired => new(StatusCodes.Status403Forbidden, "LLM key required", "llm_key_required", ex.Message, null),
            LlmFailureKind.KeyUnreadable => new(StatusCodes.Status409Conflict, "Saved LLM key unreadable", "llm_key_unreadable", ex.Message, null),
            _ => new(StatusCodes.Status502BadGateway, "LLM provider rejected the request", "llm_request_rejected", ex.Message, null),
        };
    }

    public static IResult ToResult(LlmAccessException ex, bool onSave = false)
    {
        var m = Map(ex, onSave);
        var result = ProblemResults.Coded(m.Status, m.Title, m.Detail, m.Code);
        return m.RetryAfter is { } retry ? new WithRetryAfter(result, retry) : result;
    }

    private sealed class WithRetryAfter(IResult inner, TimeSpan retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return inner.ExecuteAsync(httpContext);
        }
    }
}
