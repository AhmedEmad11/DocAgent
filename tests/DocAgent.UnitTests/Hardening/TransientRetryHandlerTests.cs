using System.Net;
using DocAgent.Infrastructure.Llm;

namespace DocAgent.UnitTests.Hardening;

public sealed class TransientRetryHandlerTests
{
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = steps[Math.Min(Calls, steps.Length - 1)];
            Calls++;
            return Task.FromResult(step());
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage Refused() => throw new HttpRequestException("connection refused");

    private static HttpClient Client(ScriptedHandler inner, int retries) =>
        new(new TransientRetryHandler(inner, retries, TimeSpan.FromMilliseconds(5)), disposeHandler: true) { BaseAddress = new Uri("http://ollama.test") };

    private static Task<HttpResponseMessage> Post(HttpClient client) => client.PostAsync("/api/chat", new StringContent("{}"));

    [Fact]
    public async Task Connection_failures_are_retried_until_the_server_answers()
    {
        var inner = new ScriptedHandler(Refused, Refused, () => Status(HttpStatusCode.OK));
        using var client = Client(inner, retries: 2);

        var response = await Post(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Bad_gateway_unavailable_and_gateway_timeout_are_retried()
    {
        var inner = new ScriptedHandler(
            () => Status(HttpStatusCode.BadGateway), () => Status(HttpStatusCode.ServiceUnavailable), () => Status(HttpStatusCode.GatewayTimeout), () => Status(HttpStatusCode.OK));
        using var client = Client(inner, retries: 3);

        Assert.Equal(HttpStatusCode.OK, (await Post(client)).StatusCode);
        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public async Task After_the_last_retry_the_original_failure_surfaces()
    {
        var inner = new ScriptedHandler(Refused);
        using var client = Client(inner, retries: 2);
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(client));
        Assert.Equal(3, inner.Calls); // first try + 2 retries

        var busy = new ScriptedHandler(() => Status(HttpStatusCode.ServiceUnavailable));
        using var client2 = Client(busy, retries: 1);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(client2)).StatusCode);
        Assert.Equal(2, busy.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Other_statuses_are_returned_at_once(HttpStatusCode status)
    {
        var inner = new ScriptedHandler(() => Status(status));
        using var client = Client(inner, retries: 3);

        Assert.Equal(status, (await Post(client)).StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Zero_retries_means_a_single_attempt()
    {
        var inner = new ScriptedHandler(Refused);
        using var client = Client(inner, retries: 0);
        await Assert.ThrowsAsync<HttpRequestException>(() => Post(client));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_retried()
    {
        var inner = new ScriptedHandler(Refused);
        using var client = new HttpClient(new TransientRetryHandler(inner, 3, TimeSpan.FromSeconds(30))) { BaseAddress = new Uri("http://ollama.test") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsync("/api/chat", new StringContent("{}"), cts.Token));
        Assert.True(inner.Calls <= 1);
    }
}
