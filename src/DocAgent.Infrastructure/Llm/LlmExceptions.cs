namespace DocAgent.Infrastructure.Llm;

/// <summary>Why a request to the user's LLM route failed in a way the user can act on (SRS KEY-7).</summary>
public enum LlmFailureKind { InvalidKey, NoCredit, RateLimited, Unavailable, UnknownModel, Rejected, KeyRequired, KeyUnreadable }

/// <summary>
/// Base of the errors that the API turns into specific Problem Details. Messages are fixed texts: they never carry the
/// provider's response, the key, or an inner exception (SRS SEC-2).
/// </summary>
public class LlmAccessException(LlmFailureKind kind, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public LlmFailureKind Kind { get; } = kind;

    /// <summary>The provider's Retry-After, when it gave one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The first <see cref="LlmAccessException"/> in the exception or its inner exceptions.</summary>
    public static LlmAccessException? Find(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < 8; depth++, ex = ex.InnerException)
        {
            if (ex is LlmAccessException found)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>A provider answered with an error status, or could not be reached.</summary>
public sealed class LlmProviderException(LlmFailureKind kind, int? statusCode = null, TimeSpan? retryAfter = null)
    : LlmAccessException(kind, MessageFor(kind), retryAfter)
{
    /// <summary>The provider's HTTP status (for logging); null when it was not reached.</summary>
    public int? StatusCode { get; } = statusCode;

    public static string MessageFor(LlmFailureKind kind) => kind switch
    {
        LlmFailureKind.InvalidKey => "The provider rejected the API key. Check that it is correct, active and allowed to use this model.",
        LlmFailureKind.NoCredit => "The provider reports that the account has no credit or quota left. Add credit or use another key.",
        LlmFailureKind.RateLimited => "The provider is rate limiting this key. Try again later.",
        LlmFailureKind.Unavailable => "The provider is not available right now. Try again later.",
        LlmFailureKind.UnknownModel => "The provider does not know this model, or the key cannot use it. Choose a model from the model list.",
        _ => "The provider rejected the request.",
    };
}

/// <summary>The user has no saved key and no fallback route is available for this environment.</summary>
public sealed class NoLlmKeyException()
    : LlmAccessException(LlmFailureKind.KeyRequired, "Add your own LLM API key first (PUT /api/v1/me/llm-key). The shared demo model is not available yet.");

/// <summary>The saved key cannot be decrypted (for example the key ring changed); the user has to enter it again.</summary>
public sealed class StoredLlmKeyUnreadableException()
    : LlmAccessException(LlmFailureKind.KeyUnreadable, "Your saved API key can no longer be read. Save it again (PUT /api/v1/me/llm-key).");
