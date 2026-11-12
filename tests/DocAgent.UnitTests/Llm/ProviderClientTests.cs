using DocAgent.TestSupport;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace DocAgent.UnitTests.Llm;

/// <summary>Each provider as an IChatClient over mocked HTTP: endpoint, auth header, model, tool calling (SRS KEY-8, MAIN-1).</summary>
public sealed class ProviderClientTests
{
    public static IEnumerable<object[]> Providers() => LlmProviders.All.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Request_goes_to_the_fixed_endpoint_with_the_right_auth_header_and_model(LlmProvider provider)
    {
        var clients = LlmTest.Clients(_ => StubTransport.Json(ProviderJson.Text(provider, "hello")), out var transport);
        using var client = LlmTest.Providers(clients).Create(provider, "the-model", LlmTest.SecretKey);

        var response = await client.GetResponseAsync("hi", new ChatOptions { MaxOutputTokens = 16 });

        Assert.Equal("hello", response.Text);
        var request = Assert.Single(transport.Requests);
        var expectedUrl = provider switch
        {
            LlmProvider.OpenRouter => "https://openrouter.ai/api/v1/chat/completions",
            LlmProvider.OpenAI => "https://api.openai.com/v1/chat/completions",
            LlmProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            _ => "https://api.anthropic.com/v1/messages",
        };
        Assert.Equal(expectedUrl, request.Url.GetLeftPart(UriPartial.Path));
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.DoesNotContain(LlmTest.SecretKey, request.Url.ToString()); // never in the URL

        // The official Anthropic SDK (13.x) sends ApiKey as "Authorization: Bearer" (documented on ClientOptions.ApiKey); no live check was possible.
        Assert.Equal("Bearer " + LlmTest.SecretKey, request.Headers["authorization"]);
        if (provider == LlmProvider.Anthropic)
        {
            Assert.True(request.Headers.ContainsKey("anthropic-version"));
            Assert.Equal(LlmTest.SecretKey, request.Headers["x-api-key"]);
        }

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("the-model", body.RootElement.GetProperty("model").GetString());
        Assert.DoesNotContain(LlmTest.SecretKey, request.Body);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Tool_calling_round_trip_works_through_function_invocation(LlmProvider provider)
    {
        var clients = LlmTest.Clients(request => StubTransport.Json(
            request.Body.Contains("the-secret-word-is-swordfish")
                ? ProviderJson.Text(provider, "The word is swordfish")
                : ProviderJson.ToolCall(provider, "get_word", """{"topic":"fish"}""")), out var transport);
        using var provided = LlmTest.Providers(clients).Create(provider, "m", LlmTest.SecretKey);
        var seenTopic = "";
        var tool = AIFunctionFactory.Create((string topic) =>
        {
            seenTopic = topic;
            return "the-secret-word-is-swordfish";
        }, "get_word", "Returns the secret word for a topic.");
        using var client = provided.AsBuilder().UseFunctionInvocation().Build();

        var response = await client.GetResponseAsync("What is the word?", new ChatOptions { Tools = [tool], MaxOutputTokens = 200 });

        Assert.Equal("The word is swordfish", response.Text);
        Assert.Equal("fish", seenTopic);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Contains("get_word", transport.Requests[0].Body); // the tool was offered to the model
        Assert.Contains("swordfish", transport.Requests[1].Body); // and its result was sent back
    }

    [Fact]
    public async Task A_request_to_any_other_host_is_refused()
    {
        var clients = LlmTest.Clients(_ => StubTransport.Json("{}"), out var transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            clients.For(LlmProvider.OpenAI).GetAsync("https://evil.example/v1/chat/completions"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            clients.For(LlmProvider.OpenAI).GetAsync("http://api.openai.com/v1/models")); // plain http
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            clients.For(LlmProvider.Anthropic).GetAsync("https://api.openai.com/v1/models")); // another provider's host

        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void Endpoints_are_fixed_constants()
    {
        Assert.Equal("https://openrouter.ai/api/v1/", LlmProviders.Endpoint(LlmProvider.OpenRouter).ToString());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/", LlmProviders.Endpoint(LlmProvider.Gemini).ToString());
        Assert.All(LlmProviders.All, p => Assert.Equal(Uri.UriSchemeHttps, LlmProviders.Endpoint(p).Scheme));
    }

    [Theory]
    [InlineData("openrouter", true)]
    [InlineData("OpenAI", true)]
    [InlineData(" gemini ", true)]
    [InlineData("anthropic", true)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("ollama", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("openai,anthropic", false)]
    public void Provider_names_are_parsed_strictly(string? value, bool ok) => Assert.Equal(ok, LlmProviders.TryParse(value, out _));

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Validation_makes_one_small_call_and_passes_provider_errors_on(LlmProvider provider)
    {
        var clients = LlmTest.Clients(_ => StubTransport.Json(ProviderJson.Text(provider, "ok")), out var transport);
        var validator = new LlmKeyValidator(LlmTest.Providers(clients));

        await validator.ValidateAsync(provider, "m", LlmTest.SecretKey, default);

        var request = Assert.Single(transport.Requests);
        Assert.Contains("16", request.Body); // max tokens cap

        var failing = LlmTest.Clients(_ => StubTransport.Json("{}", System.Net.HttpStatusCode.Unauthorized), out _);
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() =>
            new LlmKeyValidator(LlmTest.Providers(failing)).ValidateAsync(provider, "m", LlmTest.SecretKey, default));
        Assert.Equal(LlmFailureKind.InvalidKey, ex.Kind);
    }
}
