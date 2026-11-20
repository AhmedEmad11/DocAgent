using System.Net;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using DocAgent.TestSupport;
using Microsoft.Extensions.AI;

namespace DocAgent.UnitTests.Llm;

/// <summary>REL-2: hosted provider calls get exactly one retry on 5xx and connection failures, never on 429 or other 4xx.</summary>
public sealed class ProviderRetryTests
{
    public static IEnumerable<object[]> Providers() => LlmProviders.All.Select(p => new object[] { p });

    private static (IChatClient Client, StubTransport Transport) Create(LlmProvider provider, Func<int, HttpResponseMessage> respondForAttempt)
    {
        var attempt = 0;
        var clients = LlmTest.Clients(_ => respondForAttempt(Interlocked.Increment(ref attempt)), out var transport);
        return (LlmTest.Providers(clients).Create(provider, "m", LlmTest.SecretKey), transport);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_503_is_retried_once_and_the_second_answer_is_used(LlmProvider provider)
    {
        var (client, transport) = Create(provider, n => n == 1 ? StubTransport.Json("{}", HttpStatusCode.ServiceUnavailable) : StubTransport.Json(ProviderJson.Text(provider, "ok")));
        using var _ = client;

        Assert.Equal("ok", (await client.GetResponseAsync("hi")).Text);

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(transport.Requests[0].Body, transport.Requests[1].Body); // the same request is repeated
    }

    [Fact]
    public async Task Anthropic_529_overloaded_is_retried_once()
    {
        var (client, transport) = Create(LlmProvider.Anthropic, n => n == 1 ? StubTransport.Json("{}", (HttpStatusCode)529) : StubTransport.Json(ProviderJson.Text(LlmProvider.Anthropic, "ok")));
        using var _ = client;

        Assert.Equal("ok", (await client.GetResponseAsync("hi")).Text);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Two_503s_give_the_mapped_error_after_exactly_two_calls(LlmProvider provider)
    {
        var (client, transport) = Create(provider, _ => StubTransport.Json("{}", HttpStatusCode.ServiceUnavailable));
        using var _ = client;

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(LlmFailureKind.Unavailable, ex.Kind);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_connection_failure_is_retried_once(LlmProvider provider)
    {
        var calls = 0;
        var clients = new LlmHttpClients(_ => new FlakyHandler(() => ++calls == 1 ? throw new HttpRequestException("refused") : StubTransport.Json(ProviderJson.Text(provider, "ok"))), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50));
        using var client = LlmTest.Providers(clients).Create(provider, "m", LlmTest.SecretKey);

        Assert.Equal("ok", (await client.GetResponseAsync("hi")).Text);
        Assert.Equal(2, calls);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Two_connection_failures_give_unavailable_after_two_calls(LlmProvider provider)
    {
        var calls = 0;
        var clients = new LlmHttpClients(_ => new FlakyHandler(() =>
        {
            calls++;
            throw new HttpRequestException("refused " + LlmTest.SecretKey);
        }), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(50));
        using var client = LlmTest.Providers(clients).Create(provider, "m", LlmTest.SecretKey);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(LlmFailureKind.Unavailable, ex.Kind);
        Assert.DoesNotContain(LlmTest.SecretKey, ex.ToString());
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(LlmProvider.OpenRouter, 429)]
    [InlineData(LlmProvider.Anthropic, 429)]
    [InlineData(LlmProvider.OpenAI, 429)]
    [InlineData(LlmProvider.Gemini, 429)]
    [InlineData(LlmProvider.OpenAI, 400)]
    [InlineData(LlmProvider.OpenAI, 401)]
    [InlineData(LlmProvider.Anthropic, 401)]
    [InlineData(LlmProvider.Gemini, 403)]
    [InlineData(LlmProvider.OpenRouter, 402)]
    [InlineData(LlmProvider.OpenRouter, 404)]
    [InlineData(LlmProvider.OpenAI, 422)]
    public async Task Rate_limits_and_client_errors_are_never_retried(LlmProvider provider, int status)
    {
        var (client, transport) = Create(provider, _ => StubTransport.Json("{}", (HttpStatusCode)status));
        using var _ = client;

        await Assert.ThrowsAsync<LlmProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task A_huge_retry_after_on_a_5xx_is_capped_so_the_caller_is_not_stalled()
    {
        var clients = LlmTest.Clients(_ =>
        {
            var response = StubTransport.Json("{}", HttpStatusCode.BadGateway);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return response;
        }, out var transport);
        using var client = LlmTest.Providers(clients).Create(LlmProvider.OpenAI, "m", LlmTest.SecretKey);

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<LlmProviderException>(() => client.GetResponseAsync("hi"));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_during_the_backoff_is_not_retried_or_mapped()
    {
        var (client, transport) = Create(LlmProvider.OpenAI, _ => StubTransport.Json("{}", HttpStatusCode.ServiceUnavailable));
        using var _ = client;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("hi", cancellationToken: cts.Token));

        Assert.True(transport.Requests.Count <= 1);
    }

    [Fact]
    public void The_default_backoff_cap_is_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ProviderHttpHandler.MaxRetryDelay);
    }

    private sealed class FlakyHandler(Func<HttpResponseMessage> step) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(step());
    }
}
