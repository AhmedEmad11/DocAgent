using System.Diagnostics;
using System.Net;
using System.Text;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using DocAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DocAgent.IntegrationTests;

/// <summary>Keeps every log line (category, level, message, exception text) so tests can assert that secrets never appear.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var sb = new StringBuilder().Append(category).Append(' ').Append(logLevel).Append(' ').Append(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    sb.Append(' ').Append(key).Append('=').Append(value);
                }
            }

            if (exception is not null)
            {
                sb.Append('\n').Append(exception);
            }

            lock (owner._lines)
            {
                owner._lines.Add(sb.ToString());
            }
        }
    }
}

/// <summary>Records tags and events of every Activity (all sources: ASP.NET Core, HttpClient, DocAgent.*, Microsoft.Extensions.AI).</summary>
public sealed class ActivityCapture : IDisposable
{
    private readonly List<string> _items = [];
    private readonly ActivityListener _listener;

    public ActivityCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                var sb = new StringBuilder(activity.Source.Name).Append(' ').Append(activity.DisplayName).Append(' ').Append(activity.OperationName);
                foreach (var tag in activity.TagObjects)
                {
                    sb.Append(' ').Append(tag.Key).Append('=').Append(tag.Value);
                }

                foreach (var e in activity.Events)
                {
                    sb.Append(" event:").Append(e.Name);
                    foreach (var tag in e.Tags)
                    {
                        sb.Append(' ').Append(tag.Key).Append('=').Append(tag.Value);
                    }
                }

                lock (_items)
                {
                    _items.Add(sb.ToString());
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_items)
            {
                return [.. _items];
            }
        }
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// Stands in for the four provider APIs: replaces the HTTP transport of <see cref="LlmHttpClients"/> with a scripted one.
/// Chat calls go to <see cref="Chat"/> (default: a plain "ok" in the provider's own JSON format), model lists to <see cref="Models"/>.
/// </summary>
public sealed class ProviderBackend
{
    public const string OpenRouterModels = """
        {"data":[{"id":"openai/gpt-4o-mini","name":"GPT-4o mini","supported_parameters":["tools","temperature"]},{"id":"x/no-tools","name":"No tools","supported_parameters":["temperature"]}]}
        """;

    public ProviderBackend()
    {
        Transport = new StubTransport(Respond);
    }

    public StubTransport Transport { get; }

    public Func<StubTransport.RecordedRequest, HttpResponseMessage>? Chat { get; set; }

    public Func<StubTransport.RecordedRequest, HttpResponseMessage>? Models { get; set; }

    public IReadOnlyList<StubTransport.RecordedRequest> Requests
    {
        get
        {
            lock (Transport.Requests)
            {
                return [.. Transport.Requests];
            }
        }
    }

    public void Reset()
    {
        lock (Transport.Requests)
        {
            Transport.Requests.Clear();
        }

        Chat = null;
        Models = null;
    }

    public static LlmProvider ProviderOf(Uri url) => url.Host switch
    {
        "openrouter.ai" => LlmProvider.OpenRouter,
        "api.anthropic.com" => LlmProvider.Anthropic,
        "api.openai.com" => LlmProvider.OpenAI,
        _ => LlmProvider.Gemini,
    };

    public static HttpResponseMessage Error(HttpStatusCode status, string body = "{}", TimeSpan? retryAfter = null)
    {
        var response = StubTransport.Json(body, status);
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        }

        return response;
    }

    private HttpResponseMessage Respond(StubTransport.RecordedRequest request)
    {
        if (request.Method == HttpMethod.Get)
        {
            return Models?.Invoke(request) ?? StubTransport.Json(ProviderOf(request.Url) == LlmProvider.OpenRouter
                ? OpenRouterModels
                : ProviderOf(request.Url) == LlmProvider.Anthropic
                    ? """{"data":[{"id":"claude-sonnet-4-5","display_name":"Claude Sonnet 4.5"}]}"""
                    : ProviderOf(request.Url) == LlmProvider.Gemini
                        ? """{"models":[{"name":"models/gemini-2.5-flash","displayName":"Gemini 2.5 Flash","supportedGenerationMethods":["generateContent"]}]}"""
                        : """{"data":[{"id":"gpt-4o"},{"id":"whisper-1"}]}""");
        }

        return Chat?.Invoke(request) ?? StubTransport.Json(ProviderJson.Text(ProviderOf(request.Url), "ok"));
    }

    /// <summary>Swaps the real provider transport for this backend.</summary>
    public Action<IServiceCollection> Register(CapturingLoggerProvider? logs = null) => services =>
    {
        services.RemoveAll<LlmHttpClients>();
        services.AddSingleton(new LlmHttpClients(_ => Transport));
        if (logs is not null)
        {
            services.AddLogging(b => b.AddProvider(logs));
        }
    };
}

public static class SecretAssert
{
    public static readonly Dictionary<string, string?> VerboseLogging = new()
    {
        ["Logging:LogLevel:Default"] = "Trace",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
        ["Logging:LogLevel:Microsoft.Extensions.AI"] = "Trace",
        ["Logging:LogLevel:System.Net.Http"] = "Trace",
        ["Logging:LogLevel:Microsoft.Extensions.Http"] = "Trace",
        ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Debug",
    };

    /// <summary>The secret (and its middle part, in case of partial masking) appears nowhere in the response, the logs or the traces.</summary>
    public static async Task NotLeakedAsync(string secret, HttpResponseMessage response, CapturingLoggerProvider logs, ActivityCapture? activities = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join('\n', response.Headers.Concat(response.Content.Headers).Select(h => h.Key + ": " + string.Join(",", h.Value)));
        NotLeaked(secret, body, headers);
        NotLeaked(secret, logs, activities);
    }

    public static void NotLeaked(string secret, CapturingLoggerProvider logs, ActivityCapture? activities = null)
    {
        Assert.DoesNotContain(logs.Lines, l => l.Contains(secret, StringComparison.Ordinal));
        if (activities is not null)
        {
            Assert.DoesNotContain(activities.Items, l => l.Contains(secret, StringComparison.Ordinal));
        }
    }

    public static void NotLeaked(string secret, params string[] texts) =>
        Assert.All(texts, t => Assert.DoesNotContain(secret, t, StringComparison.Ordinal));
}
