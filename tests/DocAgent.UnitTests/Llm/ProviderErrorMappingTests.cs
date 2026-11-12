using DocAgent.TestSupport;
using System.Net;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace DocAgent.UnitTests.Llm;

/// <summary>Provider failures become specific, sanitized errors (SRS KEY-7, SEC-2). Every case runs through the real SDK client over a stubbed transport.</summary>
public sealed class ProviderErrorMappingTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var p in LlmProviders.All)
        {
            yield return [p, 401, "", LlmFailureKind.InvalidKey];
            yield return [p, 403, "", LlmFailureKind.InvalidKey];
            yield return [p, 402, "", LlmFailureKind.NoCredit];
            yield return [p, 404, "", LlmFailureKind.UnknownModel];
            yield return [p, 429, "", LlmFailureKind.RateLimited];
            yield return [p, 500, "", LlmFailureKind.Unavailable];
            yield return [p, 502, "", LlmFailureKind.Unavailable];
            yield return [p, 503, "", LlmFailureKind.Unavailable];
            yield return [p, 422, "", LlmFailureKind.Rejected];
        }

        // Provider-specific bodies.
        yield return [LlmProvider.OpenAI, 429, """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""", LlmFailureKind.NoCredit];
        yield return [LlmProvider.OpenAI, 429, """{"error":{"message":"Rate limit reached for gpt-4o","type":"requests","code":"rate_limit_exceeded"}}""", LlmFailureKind.RateLimited];
        yield return [LlmProvider.OpenAI, 404, """{"error":{"message":"The model `gpt-9` does not exist","code":"model_not_found"}}""", LlmFailureKind.UnknownModel];
        yield return [LlmProvider.Anthropic, 400, """{"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API."}}""", LlmFailureKind.NoCredit];
        yield return [LlmProvider.Anthropic, 529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", LlmFailureKind.Unavailable];
        yield return [LlmProvider.Anthropic, 404, """{"type":"error","error":{"type":"not_found_error","message":"model: claude-x"}}""", LlmFailureKind.UnknownModel];
        yield return [LlmProvider.Gemini, 400, """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT","details":[{"reason":"API_KEY_INVALID"}]}}""", LlmFailureKind.InvalidKey];
        yield return [LlmProvider.Gemini, 429, """{"error":{"code":429,"message":"Resource has been exhausted (e.g. check quota).","status":"RESOURCE_EXHAUSTED"}}""", LlmFailureKind.RateLimited];
        yield return [LlmProvider.Gemini, 404, """{"error":{"code":404,"message":"models/gemini-9 is not found for API version v1beta","status":"NOT_FOUND"}}""", LlmFailureKind.UnknownModel];
        yield return [LlmProvider.OpenRouter, 402, """{"error":{"message":"Insufficient credits. Add more using https://openrouter.ai/credits","code":402}}""", LlmFailureKind.NoCredit];
        yield return [LlmProvider.OpenRouter, 400, """{"error":{"message":"x/y is not a valid model ID","code":400}}""", LlmFailureKind.UnknownModel];
        yield return [LlmProvider.OpenRouter, 400, """{"error":{"message":"Provider returned error","code":400}}""", LlmFailureKind.Rejected];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Chat_call_failure_maps_to_the_specific_kind_without_leaking_the_response(
        LlmProvider provider, int status, string body, LlmFailureKind expected)
    {
        // The hostile body echoes the key: it must not reach the exception.
        var echoed = (string.IsNullOrEmpty(body) ? """{"error":{"message":"bad key @KEY"}}""" : body).Replace("@KEY", LlmTest.SecretKey) + " " + LlmTest.SecretKey;
        var clients = LlmTest.Clients(_ => StubTransport.Json(echoed, (HttpStatusCode)status), out _);
        using var client = LlmTest.Providers(clients).Create(provider, "some-model", LlmTest.SecretKey);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(expected, ex.Kind);
        Assert.Equal(status, ex.StatusCode);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(LlmTest.SecretKey, ex.ToString());
        Assert.Equal(LlmProviderException.MessageFor(expected), ex.Message);
    }

    [Theory]
    [InlineData(LlmProvider.OpenRouter)]
    [InlineData(LlmProvider.Anthropic)]
    [InlineData(LlmProvider.OpenAI)]
    [InlineData(LlmProvider.Gemini)]
    public async Task Retry_after_is_passed_on_and_rate_limits_are_not_retried(LlmProvider provider)
    {
        var clients = LlmTest.Clients(_ =>
        {
            var response = StubTransport.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return response;
        }, out var transport);
        using var client = LlmTest.Providers(clients).Create(provider, "m", LlmTest.SecretKey);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(LlmFailureKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(17), ex.RetryAfter);
        Assert.Single(transport.Requests); // the SDKs' own retries are off: one call
    }

    [Theory]
    [InlineData(LlmProvider.OpenRouter)]
    [InlineData(LlmProvider.Anthropic)]
    [InlineData(LlmProvider.OpenAI)]
    [InlineData(LlmProvider.Gemini)]
    public async Task A_network_failure_means_provider_unavailable(LlmProvider provider)
    {
        var clients = new LlmHttpClients(_ => new ThrowingHandler());
        using var client = LlmTest.Providers(clients).Create(provider, "m", LlmTest.SecretKey);

        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => client.GetResponseAsync("hi"));

        Assert.Equal(LlmFailureKind.Unavailable, ex.Kind);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_turned_into_a_provider_error()
    {
        var clients = LlmTest.Clients(_ => StubTransport.Json("{}"), out _);
        using var client = LlmTest.Providers(clients).Create(LlmProvider.OpenAI, "m", LlmTest.SecretKey);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync("hi", cancellationToken: cts.Token));
    }

    [Fact]
    public void Retry_after_longer_than_ten_minutes_is_capped_and_unknown_statuses_are_rejections()
    {
        Assert.Equal(LlmFailureKind.Rejected, ProviderHttpHandler.Classify(418, ""));
        Assert.Equal(LlmFailureKind.Unavailable, ProviderHttpHandler.Classify(408, ""));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused: " + LlmTest.SecretKey);
    }
}
