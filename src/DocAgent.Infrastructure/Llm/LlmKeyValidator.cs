using DocAgent.Core.Entities;
using Microsoft.Extensions.AI;

namespace DocAgent.Infrastructure.Llm;

/// <summary>Validates a key before it is saved (SRS KEY-2): one minimal chat call with the chosen model.</summary>
public sealed class LlmKeyValidator(IProviderChatClientFactory providers)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A one-word prompt and a 16-token answer cap: about as cheap as a call can be, and it checks key, model and credit in one go.
    /// A failure is an <see cref="LlmProviderException"/>; a timeout counts as "provider unavailable".
    /// </summary>
    public async Task ValidateAsync(LlmProvider provider, string model, string apiKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var client = providers.Create(provider, model, apiKey);
        try
        {
            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word: ok")],
                new ChatOptions { MaxOutputTokens = 16, Temperature = 0 }, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new LlmProviderException(LlmFailureKind.Unavailable);
        }
    }
}
