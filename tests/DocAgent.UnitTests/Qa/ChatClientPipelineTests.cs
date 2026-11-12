using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocAgent.UnitTests.Qa;

public sealed class ChatClientPipelineTests
{
    [Fact]
    public async Task Prompt_and_answer_text_never_reach_the_logs_at_information_level()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Information));
        var client = ChatClientPipeline.Wrap(new EchoClient(), loggerFactory);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "SECRET-DOCUMENT-TEXT")]);

        Assert.DoesNotContain(logs.Messages, m => m.Contains("SECRET-DOCUMENT-TEXT") || m.Contains("SECRET-ANSWER-TEXT"));
    }

    [Fact]
    public async Task Prompt_text_is_only_logged_when_trace_is_enabled_which_the_default_config_prevents()
    {
        // Documents the reason for pinning "Microsoft.Extensions.AI" to Warning in appsettings.json.
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var client = ChatClientPipeline.Wrap(new EchoClient(), loggerFactory);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "SECRET-DOCUMENT-TEXT")]);

        Assert.Contains(logs.Messages, m => m.Contains("SECRET-DOCUMENT-TEXT"));
    }

    private sealed class EchoClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "SECRET-ANSWER-TEXT")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception) + " " + string.Join(' ', (state as IEnumerable<KeyValuePair<string, object?>>)?.Select(p => p.Value?.ToString()) ?? []));
        }
    }
}
