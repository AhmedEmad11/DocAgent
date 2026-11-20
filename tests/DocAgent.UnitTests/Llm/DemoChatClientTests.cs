using System.Net;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using DocAgent.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocAgent.UnitTests.Llm;

/// <summary>The demo model and its fallback (SRS DEMO-4) over the real OpenRouter client with mocked HTTP.</summary>
public sealed class DemoChatClientTests
{
    private const string Primary = "google/gemma-4-31b-it:free";
    private const string Fallback = "google/gemma-4-26b-a4b-it:free";

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel} {formatter(state, exception)} {exception}");
    }

    private static string ModelOf(StubTransport.RecordedRequest request) =>
        System.Text.Json.JsonDocument.Parse(request.Body).RootElement.GetProperty("model").GetString()!;

    private static (DemoChatClient Client, StubTransport Transport, ListLogger Logger, int[] Refunds) Create(
        Func<string, HttpResponseMessage> respondForModel, string? fallback = Fallback)
    {
        var clients = LlmTest.Clients(r => respondForModel(ModelOf(r)), out var transport);
        var providers = LlmTest.Providers(clients);
        var logger = new ListLogger();
        var refunds = new int[1];
        var client = new DemoChatClient(
            providers.Create(LlmProvider.OpenRouter, Primary, LlmTest.SecretKey), Primary,
            fallback is null ? null : providers.Create(LlmProvider.OpenRouter, fallback, LlmTest.SecretKey), fallback,
            logger, () =>
            {
                refunds[0]++;
                return Task.CompletedTask;
            });
        return (client, transport, logger, refunds);
    }

    private static HttpResponseMessage Ok(string text) => StubTransport.Json(ProviderJson.Text(LlmProvider.OpenRouter, text));

    private static HttpResponseMessage Fail(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = StubTransport.Json("{}", status);
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        }

        return response;
    }

    [Fact]
    public async Task The_primary_model_answers_and_is_reported()
    {
        var (client, transport, _, refunds) = Create(_ => Ok("hi"));

        Assert.Equal("hi", (await client.GetResponseAsync("q")).Text);

        Assert.Equal(Primary, client.CurrentInfo().Model);
        Assert.Equal("demo", client.CurrentInfo().Mode);
        Assert.Single(transport.Requests);
        Assert.Equal(0, refunds[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)] // a withdrawn free model
    public async Task A_failing_primary_is_retried_once_on_the_fallback_and_the_fallback_is_reported(HttpStatusCode status)
    {
        var (client, transport, logger, refunds) = Create(model => model == Primary ? Fail(status) : Ok("from fallback"));

        Assert.Equal("from fallback", (await client.GetResponseAsync("q")).Text);

        // 5xx: the primary is tried twice (one retry in the HTTP layer, REL-2), then the fallback. 429 and 404: straight to the fallback.
        var retried = status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway;
        Assert.Equal(retried ? [Primary, Primary, Fallback] : [Primary, Fallback], transport.Requests.Select(ModelOf));
        Assert.Equal(Fallback, client.CurrentInfo().Model);
        Assert.Equal(0, refunds[0]);
        Assert.Contains(logger.Lines, l => l.StartsWith("Warning") && l.Contains(Primary));
    }

    [Fact]
    public async Task After_a_fallback_the_rest_of_the_request_stays_on_the_fallback()
    {
        var (client, transport, _, _) = Create(model => model == Primary ? Fail(HttpStatusCode.TooManyRequests) : Ok("x"));

        await client.GetResponseAsync("one");
        await client.GetResponseAsync("two");

        Assert.Equal([Primary, Fallback, Fallback], transport.Requests.Select(ModelOf));
    }

    [Fact]
    public async Task Both_models_rate_limited_gives_a_rate_limited_error_with_the_shortest_retry_after_and_gives_the_question_back()
    {
        var (client, _, _, refunds) = Create(model => Fail(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(model == Primary ? 30 : 12)));

        var ex = await Assert.ThrowsAsync<DemoUnavailableException>(() => client.GetResponseAsync("q"));

        Assert.Equal(LlmFailureKind.DemoRateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), ex.RetryAfter);
        Assert.Equal(1, refunds[0]);
    }

    [Fact]
    public async Task Both_rate_limited_without_retry_after_uses_a_default_wait()
    {
        var (client, _, _, _) = Create(_ => Fail(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<DemoUnavailableException>(() => client.GetResponseAsync("q"));

        Assert.Equal(DemoChatClient.DefaultRetryAfter, ex.RetryAfter);
    }

    [Fact]
    public async Task Both_models_failing_in_different_ways_is_unavailable()
    {
        var (client, _, _, refunds) = Create(model => Fail(model == Primary ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<DemoUnavailableException>(() => client.GetResponseAsync("q"));

        Assert.Equal(LlmFailureKind.DemoUnavailable, ex.Kind);
        Assert.Null(ex.RetryAfter);
        Assert.Equal(1, refunds[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public async Task An_invalid_operator_key_does_not_try_the_fallback_is_logged_as_an_error_without_the_key(HttpStatusCode status)
    {
        var (client, transport, logger, refunds) = Create(_ => Fail(status));

        var ex = await Assert.ThrowsAsync<DemoUnavailableException>(() => client.GetResponseAsync("q"));

        Assert.Equal(LlmFailureKind.DemoUnavailable, ex.Kind);
        Assert.Single(transport.Requests); // the same key would fail on the fallback too
        Assert.Equal(1, refunds[0]);
        Assert.Contains(logger.Lines, l => l.StartsWith("Error") && l.Contains("Llm:Demo:OpenRouterApiKey"));
        Assert.DoesNotContain(logger.Lines, l => l.Contains(LlmTest.SecretKey));
        Assert.DoesNotContain(LlmTest.SecretKey, ex.ToString());
    }

    [Fact]
    public async Task Without_a_fallback_model_a_primary_failure_is_final()
    {
        var (client, transport, _, _) = Create(_ => Fail(HttpStatusCode.TooManyRequests), fallback: null);

        await Assert.ThrowsAsync<DemoUnavailableException>(() => client.GetResponseAsync("q"));

        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task A_failure_after_an_earlier_answer_does_not_give_the_question_back()
    {
        var calls = 0;
        var (client, _, _, refunds) = Create(_ => Interlocked.Increment(ref calls) == 1 ? Ok("first") : Fail(HttpStatusCode.ServiceUnavailable));

        await client.GetResponseAsync("one"); // e.g. the agent's first turn
        await Assert.ThrowsAsync<DemoUnavailableException>(() => client.GetResponseAsync("two"));

        Assert.Equal(0, refunds[0]);
    }

    [Fact]
    public async Task The_question_is_given_back_only_once()
    {
        var (client, _, _, refunds) = Create(_ => Fail(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<DemoUnavailableException>(() => client.GetResponseAsync("one"));
        await Assert.ThrowsAsync<DemoUnavailableException>(() => client.GetResponseAsync("two"));

        Assert.Equal(1, refunds[0]);
    }

    [Fact]
    public async Task Streaming_replays_the_answer_with_the_same_fallback_rules()
    {
        var (client, _, _, _) = Create(model => model == Primary ? Fail(HttpStatusCode.TooManyRequests) : Ok("streamed"));

        var text = string.Concat(await client.GetStreamingResponseAsync("q").Select(u => u.Text).ToListAsync());

        Assert.Equal("streamed", text);
        Assert.Equal(Fallback, client.CurrentInfo().Model);
    }

    [Fact]
    public async Task Cancellation_is_not_treated_as_a_provider_failure()
    {
        var (client, _, _, refunds) = Create(_ => Ok("x"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("q", cancellationToken: cts.Token));

        Assert.Equal(0, refunds[0]);
    }
}
