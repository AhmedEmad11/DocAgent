using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocAgent.IntegrationTests;

/// <summary>A scripted <see cref="IChatClient"/> so tests never need Ollama. Records every call.</summary>
public sealed class FakeChatClient : IChatClient
{
    public sealed record Call(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

    private readonly List<Call> _calls = [];

    public Func<Call, CancellationToken, Task<ChatResponse>> Handler { get; set; } = (_, _) => Task.FromResult(Reply("Answer [1].", false));

    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public string SystemPrompt(int call = 0) => Calls[call].Messages.First(m => m.Role == ChatRole.System).Text;

    public string UserPrompt(int call = 0) => Calls[call].Messages.First(m => m.Role == ChatRole.User).Text;

    public void Reset()
    {
        lock (_calls)
        {
            _calls.Clear();
        }

        Handler = (_, _) => Task.FromResult(Reply("Answer [1].", false));
    }

    public static ChatResponse Reply(string answer, bool notFound) =>
        new(new ChatMessage(ChatRole.Assistant, System.Text.Json.JsonSerializer.Serialize(new { answer, notFound })));

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = new Call([.. messages], options);
        lock (_calls)
        {
            _calls.Add(call);
        }

        return Handler(call, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class FakeLlmClientFactory(FakeChatClient client) : ILlmClientFactory
{
    public const string ModelName = "fake-model";

    public ValueTask<LlmRoute> GetRouteAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(new LlmRoute(client, new LlmRouteInfo("development", "Ollama", ModelName)));

    /// <summary>For <c>DocAgentApiFactory</c>'s configureServices hook.</summary>
    public static Action<IServiceCollection> Register(FakeChatClient client) => services =>
    {
        services.RemoveAll<ILlmClientFactory>();
        services.AddSingleton<ILlmClientFactory>(new FakeLlmClientFactory(client));
    };
}
