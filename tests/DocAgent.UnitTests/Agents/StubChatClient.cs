using Microsoft.Extensions.AI;

namespace DocAgent.UnitTests.Agents;

/// <summary>A scripted chat client for unit tests; records every call.</summary>
public sealed class StubChatClient(Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> script) : IChatClient
{
    public sealed record Call(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

    public List<Call> Calls { get; } = [];

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add(new Call(list, options));
        return Task.FromResult(script(Calls.Count - 1, list, options));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
