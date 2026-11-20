using System.Net;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// Retries a request when the server could not be reached at all (connection refused, reset, DNS) or answered 502/503/504,
/// with exponential backoff, a small fixed number of times (SRS REL-2). Used for the local Ollama server, where a restart
/// or a model reload makes the first connection fail. Not used for the hosted providers: those calls are billed to a user's
/// key or to the shared demo key and have their own handling (no retry on 429, see PROGRESS.md Phase 5).
/// A timeout or a cancellation is never retried (the caller's deadline has to hold), and neither is any other status
/// (a 4xx or 500 will not change on a second try). Needs a buffered request body (the JSON bodies OllamaSharp sends are).
/// </summary>
public sealed class TransientRetryHandler(HttpMessageHandler inner, int maxRetries, TimeSpan baseDelay) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (attempt >= maxRetries || !IsTransientStatus(response.StatusCode))
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                // fall through to the delay and the next attempt
            }

            await Task.Delay(baseDelay * Math.Pow(2, attempt), cancellationToken);
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
