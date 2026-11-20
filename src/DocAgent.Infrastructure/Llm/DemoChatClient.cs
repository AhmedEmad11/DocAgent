using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocAgent.Infrastructure.Llm;

/// <summary>
/// The demo model with its fallback (SRS DEMO-4). A call that fails because the first model is rate limited, unavailable
/// or withdrawn is repeated once on the fallback model, and the request then stays on the fallback so the answer and the
/// reported model agree. A bad operator key (401/402/403) is an operator error: no fallback (same key), the error is logged
/// without any detail from the provider, and the user gets <see cref="DemoUnavailableException"/>.
/// If nothing was answered in this request, the charged question is given back (<c>refund</c>).
/// </summary>
public sealed class DemoChatClient : IChatClient
{
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    private readonly IChatClient _primary;
    private readonly IChatClient? _fallback;
    private readonly string _primaryModel;
    private readonly string? _fallbackModel;
    private readonly ILogger _logger;
    private readonly Func<Task> _refund;
    private volatile bool _onFallback;
    private int _answered;
    private int _refunded;

    public DemoChatClient(IChatClient primary, string primaryModel, IChatClient? fallback, string? fallbackModel, ILogger logger, Func<Task> refund)
    {
        _primary = primary;
        _primaryModel = primaryModel;
        _fallback = fallback;
        _fallbackModel = fallbackModel;
        _logger = logger;
        _refund = refund;
    }

    /// <summary>The model that answered (or is answering) this request.</summary>
    public LlmRouteInfo CurrentInfo() => new("demo", "OpenRouter", _onFallback && _fallbackModel is not null ? _fallbackModel : _primaryModel);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        return RunAsync(client => client.GetResponseAsync(list, options, cancellationToken));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The agent does not stream model output; keep the fallback logic in one place by answering whole and replaying.
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        _primary.Dispose();
        _fallback?.Dispose();
    }

    private async Task<T> RunAsync<T>(Func<IChatClient, Task<T>> call)
    {
        LlmProviderException? first = null;
        if (!_onFallback)
        {
            try
            {
                var result = await call(_primary);
                Interlocked.Exchange(ref _answered, 1);
                return result;
            }
            catch (LlmProviderException ex) when (_fallback is not null && CanFallBack(ex.Kind))
            {
                first = ex;
                _logger.LogWarning("Demo model {Model} failed ({Kind}, HTTP {Status}); trying the fallback model", _primaryModel, ex.Kind, ex.StatusCode);
                _onFallback = true;
            }
            catch (LlmProviderException ex)
            {
                throw await FailAsync(ex);
            }
        }

        try
        {
            var result = await call(_fallback!);
            Interlocked.Exchange(ref _answered, 1);
            return result;
        }
        catch (LlmProviderException ex)
        {
            throw await FailAsync(first is null ? [ex] : [first, ex]);
        }
    }

    private static bool CanFallBack(LlmFailureKind kind) =>
        kind is LlmFailureKind.RateLimited or LlmFailureKind.Unavailable or LlmFailureKind.UnknownModel or LlmFailureKind.Rejected;

    private async Task<DemoUnavailableException> FailAsync(params LlmProviderException[] errors)
    {
        if (Volatile.Read(ref _answered) == 0 && Interlocked.Exchange(ref _refunded, 1) == 0)
        {
            try
            {
                await _refund();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("The demo question could not be given back ({ExceptionType})", ex.GetType().Name);
            }
        }

        // Operator problems: the key (401/403, 402) or a request the provider rejects. Logged for the operator; never shown.
        if (errors.FirstOrDefault(e => e.Kind is LlmFailureKind.InvalidKey or LlmFailureKind.NoCredit or LlmFailureKind.Rejected) is { } operatorError)
        {
            _logger.LogError(
                "Demo mode is misconfigured or the operator's OpenRouter account is unusable: {Kind} (HTTP {Status}). Check Llm:Demo:OpenRouterApiKey, its credit and the demo model IDs.",
                operatorError.Kind, operatorError.StatusCode);
            return new DemoUnavailableException(rateLimited: false);
        }

        if (errors.All(e => e.Kind == LlmFailureKind.RateLimited))
        {
            var wait = errors.Select(e => e.RetryAfter).Where(r => r is not null).Select(r => r!.Value).DefaultIfEmpty(DefaultRetryAfter).Min();
            _logger.LogWarning("The demo models are rate limited; asking the user to retry in {Seconds} s", (int)wait.TotalSeconds);
            return new DemoUnavailableException(rateLimited: true, wait);
        }

        _logger.LogWarning("The demo models are unavailable ({Kinds})", string.Join(", ", errors.Select(e => e.Kind)));
        return new DemoUnavailableException(rateLimited: false);
    }
}
