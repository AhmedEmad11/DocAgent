using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// Sits directly on a provider SDK client. Whatever the SDK throws is replaced by a clean <see cref="LlmProviderException"/>
/// (no inner exception, no message from the provider), so the logging and telemetry layers above, the agent and the API
/// only ever see sanitized errors (SRS SEC-2, KEY-7). Network failures become "provider unavailable".
/// </summary>
public sealed class ProviderErrorChatClient(IChatClient inner, ILogger logger) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (Translate(ex, cancellationToken) is { } translated)
        {
            throw translated;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        yield break;
                    }
                }
                catch (Exception ex) when (Translate(ex, cancellationToken) is { } translated)
                {
                    throw translated;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private LlmAccessException? Translate(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            return null;
        }

        var found = LlmAccessException.Find(ex);
        if (found is LlmProviderException provider)
        {
            logger.LogWarning("LLM provider call failed: {Kind} (HTTP {Status})", provider.Kind, provider.StatusCode);
            return new LlmProviderException(provider.Kind, provider.StatusCode, provider.RetryAfter);
        }

        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or System.Net.Sockets.SocketException or TimeoutException)
            {
                logger.LogWarning("LLM provider call failed: provider unreachable ({ExceptionType})", e.GetType().Name);
                return new LlmProviderException(LlmFailureKind.Unavailable);
            }
        }

        return null;
    }
}
