using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using DocAgent.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DocAgent.IntegrationTests.AgentScript;

namespace DocAgent.IntegrationTests;

/// <summary>No-key route that tests can switch off, standing in for "outside Development" (<see cref="NoNoKeyRouteProvider"/>).</summary>
public sealed class SwitchableNoKeyRoute(FakeChatClient client) : INoKeyRouteProvider
{
    public bool Enabled { get; set; } = true;

    public ValueTask<LlmRoute?> GetRouteAsync(CancellationToken ct = default) => ValueTask.FromResult(
        Enabled ? new LlmRoute(client, new LlmRouteInfo("development", "Ollama", FakeLlmClientFactory.ModelName), null, null) : null);
}

/// <summary>The real per-request route selection with the provider HTTP mocked; no-key users get the scripted fake model.</summary>
public sealed class ByokScenario(PostgresFixture postgres) : QaScenario(postgres)
{
    public override string Mode => "Agent";

    public ProviderBackend Backend { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();
    public ActivityCapture Activities { get; } = new();
    public SwitchableNoKeyRoute NoKey { get; private set; } = null!;

    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>(SecretAssert.VerboseLogging)
    {
        ["Agent:Limits:Byok:MaxToolCalls"] = "2",
        ["Agent:Limits:Byok:TimeoutSeconds"] = "90",
        ["Agent:Limits:Development:MaxToolCalls"] = "9",
    };

    protected override Action<IServiceCollection> ConfigureServices()
    {
        NoKey = new SwitchableNoKeyRoute(Llm);
        var register = Backend.Register(Logs);
        return services =>
        {
            register(services);
            services.RemoveAll<INoKeyRouteProvider>();
            services.AddSingleton<INoKeyRouteProvider>(NoKey);
        };
    }

    public new async Task DisposeAsync()
    {
        Activities.Dispose();
        await base.DisposeAsync();
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ByokAskTests : IClassFixture<ByokScenario>, IAsyncLifetime
{
    private const string Key = "sk-or-v1-ASKKEY-9f8e7d6c5b4a39281706f5e4d3c2b1a0";

    private readonly ByokScenario _s;

    public ByokAskTests(ByokScenario scenario)
    {
        _s = scenario;
        _s.Llm.Reset();
        _s.Backend.Reset();
        _s.NoKey.Enabled = true;
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCall("search_documents", Args(("query", QaScenario.PricingQuestion)))
            : Text("The Pro plan costs 20 dollars per month [1]."));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var user in new[] { _s.UserA, _s.UserB })
        {
            await user.Client.DeleteAsync("/api/v1/me/llm-key");
        }
    }

    private static Task<HttpResponseMessage> Ask(TestUser user) =>
        user.Client.PostAsJsonAsync("/api/v1/ask", new { question = QaScenario.PricingQuestion });

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private async Task SaveKeyAsync(TestUser user, string provider = "openrouter", string model = "openai/gpt-4o-mini", string apiKey = Key)
    {
        Assert.Equal(HttpStatusCode.OK, (await user.Client.PutAsJsonAsync("/api/v1/me/llm-key", new { provider, model, apiKey })).StatusCode);
        _s.Backend.Reset();
    }

    /// <summary>The provider as an agent model: search first, then answer, and the verifier gets "supported". Works for both wire formats.</summary>
    private static Func<StubTransport.RecordedRequest, HttpResponseMessage> ScriptedModel(Action<StubTransport.RecordedRequest>? seen = null) => request =>
    {
        seen?.Invoke(request);
        var provider = ProviderBackend.ProviderOf(request.Url);
        if (request.Body.Contains("strict fact checker"))
        {
            return StubTransport.Json(ProviderJson.Text(provider, """{"claims":[{"id":1,"supported":true,"source":1}],"answersQuestion":true}"""));
        }

        var toolResultSeen = request.Body.Contains("tool_result") || request.Body.Contains("\"role\":\"tool\"");
        return StubTransport.Json(toolResultSeen
            ? ProviderJson.Text(provider, "The Pro plan costs 20 dollars per month [1].")
            : ProviderJson.ToolCall(provider, "search_documents", $$"""{"query":"{{QaScenario.PricingQuestion}}"}"""));
    };

    [Fact]
    public async Task Without_a_saved_key_the_no_key_route_is_used()
    {
        var body = await Json(await Ask(_s.UserA));

        Assert.Equal("development", body.GetProperty("mode").GetString());
        Assert.Equal("Ollama", body.GetProperty("provider").GetString());
        Assert.Empty(_s.Backend.Requests);
        Assert.NotEmpty(_s.Llm.Calls);
    }

    [Theory]
    [InlineData("openrouter", "openai/gpt-4o-mini", "OpenRouter", "openrouter.ai")]
    [InlineData("anthropic", "claude-sonnet-4-5", "Anthropic", "api.anthropic.com")]
    [InlineData("openai", "gpt-4o", "OpenAI", "api.openai.com")]
    [InlineData("gemini", "gemini-2.5-flash", "Gemini", "generativelanguage.googleapis.com")]
    public async Task With_a_saved_key_the_phase_4_agent_runs_unchanged_on_the_users_provider(
        string provider, string model, string providerName, string host)
    {
        await SaveKeyAsync(_s.UserA, provider, model);
        _s.Backend.Chat = ScriptedModel();

        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("The Pro plan costs 20 dollars per month [1].", body.GetProperty("answer").GetString());
        Assert.False(body.GetProperty("notFound").GetBoolean());
        Assert.Equal(1, body.GetProperty("toolCalls").GetInt32());
        Assert.Equal("byok", body.GetProperty("mode").GetString());
        Assert.Equal(providerName, body.GetProperty("provider").GetString());
        Assert.Equal(model, body.GetProperty("model").GetString());
        var citation = Assert.Single(body.GetProperty("citations").EnumerateArray().ToList());
        Assert.Equal(_s.PlansPdf, citation.GetProperty("documentId").GetGuid());

        var calls = _s.Backend.Requests;
        Assert.Equal(3, calls.Count); // tool request, answer, verification
        Assert.All(calls, c => Assert.Equal(host, c.Url.Host));
        Assert.All(calls, c => Assert.Contains(Key, string.Join(' ', c.Headers.Values)));
        foreach (var tool in new[] { "search_documents", "get_chunk_context", "get_document_outline", "summarize_document", "compare_documents" })
        {
            Assert.Contains(tool, calls[0].Body); // the same five tools reach the provider
        }

        Assert.Empty(_s.Llm.Calls); // the no-key model was not used
        await SecretAssert.NotLeakedAsync(Key, response, _s.Logs, _s.Activities);
    }

    [Fact]
    public async Task Byok_routes_use_the_byok_agent_limits()
    {
        await SaveKeyAsync(_s.UserA);
        var toolOffers = 0;
        // A model that keeps searching as long as tools are offered (Agent:Limits:Byok:MaxToolCalls = 2 in this scenario).
        _s.Backend.Chat = request =>
        {
            var provider = ProviderBackend.ProviderOf(request.Url);
            if (request.Body.Contains("strict fact checker"))
            {
                return StubTransport.Json(ProviderJson.Text(provider, """{"claims":[{"id":1,"supported":true,"source":1}],"answersQuestion":true}"""));
            }

            if (request.Body.Contains("\"tools\""))
            {
                Interlocked.Increment(ref toolOffers);
                return StubTransport.Json(ProviderJson.ToolCall(provider, "search_documents", $$"""{"query":"pro plan {{toolOffers}}"}"""));
            }

            return StubTransport.Json(ProviderJson.Text(provider, "The Pro plan costs 20 dollars per month [1]."));
        };

        var body = await Json(await Ask(_s.UserA));

        Assert.Equal(2, body.GetProperty("toolCalls").GetInt32()); // not 9 (development) and not 10
        Assert.True(body.GetProperty("incomplete").GetBoolean());
        Assert.Equal("byok", body.GetProperty("mode").GetString());
    }

    public static IEnumerable<object?[]> ProviderFailures() =>
    [
        [HttpStatusCode.Unauthorized, null, 422, "llm_key_invalid", null],
        [HttpStatusCode.PaymentRequired, null, 402, "llm_no_credit", null],
        [HttpStatusCode.TooManyRequests, null, 429, "llm_rate_limited", "9"],
        [HttpStatusCode.TooManyRequests, """{"error":{"code":"insufficient_quota"}}""", 402, "llm_no_credit", null],
        [HttpStatusCode.NotFound, null, 422, "llm_model_unknown", null],
        [HttpStatusCode.BadGateway, null, 503, "llm_unavailable", null],
    ];

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task Provider_failures_on_ask_summary_and_compare_are_specific_and_do_not_fall_back_or_leak(
        HttpStatusCode providerStatus, string? providerBody, int expectedStatus, string expectedCode, string? retryAfter)
    {
        await SaveKeyAsync(_s.UserA);
        _s.Backend.Chat = _ => ProviderBackend.Error(
            providerStatus, providerBody ?? "{\"error\":{\"message\":\"Incorrect API key provided: " + Key + "\"}}",
            retryAfter is null ? null : TimeSpan.FromSeconds(int.Parse(retryAfter)));

        var requests = new (string Name, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("ask", () => Ask(_s.UserA)),
            ("stream", () => _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion })),
            ("summary", () => _s.UserA.Client.PostAsJsonAsync($"/api/v1/documents/{_s.PlansPdf}/summary", new { })),
            ("compare", () => _s.UserA.Client.PostAsJsonAsync("/api/v1/documents/compare", new { documentIdA = _s.PlansPdf, documentIdB = _s.HolidaysTxt })),
        };
        foreach (var (name, send) in requests)
        {
            _s.Backend.Reset();
            _s.Backend.Chat = _ => ProviderBackend.Error(
                providerStatus, providerBody ?? "{\"error\":{\"message\":\"Incorrect API key provided: " + Key + "\"}}",
                retryAfter is null ? null : TimeSpan.FromSeconds(int.Parse(retryAfter)));

            var response = await send();

            if (name == "stream")
            {
                // The stream is already open when the provider answers: the error is an event (status and code inside), not the HTTP status.
                var text = await response.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("event: error", text);
                Assert.Contains($"\"status\":{expectedStatus}", text);
                Assert.Contains($"\"code\":\"{expectedCode}\"", text);
                Assert.True(_s.Backend.Requests.Count == 1, $"stream: expected one provider call, got {_s.Backend.Requests.Count}");
                await SecretAssert.NotLeakedAsync(Key, response, _s.Logs, _s.Activities);
                continue;
            }

            Assert.True(expectedStatus == (int)response.StatusCode, $"{name}: expected {expectedStatus}, got {(int)response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(expectedCode, (await Json(response)).GetProperty("code").GetString());
            Assert.Equal(retryAfter, response.Headers.TryGetValues("Retry-After", out var v) ? v.Single() : null);
            Assert.True(_s.Backend.Requests.Count == 1, $"{name}: expected one provider call, got {_s.Backend.Requests.Count}"); // no single-pass fallback, no retry
            await SecretAssert.NotLeakedAsync(Key, response, _s.Logs, _s.Activities);
        }
    }

    [Fact]
    public async Task A_provider_failure_after_the_first_tool_turn_is_reported_not_swallowed_by_the_single_pass_fallback()
    {
        await SaveKeyAsync(_s.UserA);
        var n = 0;
        _s.Backend.Chat = request => Interlocked.Increment(ref n) == 1
            ? StubTransport.Json(ProviderJson.ToolCall(ProviderBackend.ProviderOf(request.Url), "search_documents", $$"""{"query":"{{QaScenario.PricingQuestion}}"}"""))
            : ProviderBackend.Error(HttpStatusCode.TooManyRequests, "{}", TimeSpan.FromSeconds(3));

        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("3", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(2, _s.Backend.Requests.Count);
    }

    [Fact]
    public async Task A_network_failure_to_the_provider_is_a_503()
    {
        await SaveKeyAsync(_s.UserA);
        _s.Backend.Chat = _ => throw new HttpRequestException("name resolution failed");

        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("llm_unavailable", (await Json(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Another_users_key_is_never_used_and_deleting_the_key_returns_to_the_no_key_route()
    {
        await SaveKeyAsync(_s.UserA);
        _s.Backend.Chat = ScriptedModel();

        var asB = await Json(await Ask(_s.UserB)); // B has documents but no key
        Assert.Equal("development", asB.GetProperty("mode").GetString());
        Assert.Empty(_s.Backend.Requests);

        Assert.Equal("byok", (await Json(await Ask(_s.UserA))).GetProperty("mode").GetString());
        Assert.NotEmpty(_s.Backend.Requests);

        await _s.UserA.Client.DeleteAsync("/api/v1/me/llm-key");
        _s.Backend.Reset();
        Assert.Equal("development", (await Json(await Ask(_s.UserA))).GetProperty("mode").GetString());
        Assert.Empty(_s.Backend.Requests);
    }

    [Fact]
    public async Task Outside_development_a_user_without_a_key_gets_403_telling_them_to_add_one()
    {
        _s.NoKey.Enabled = false;
        var calls = new (string Name, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("ask", () => Ask(_s.UserA)),
            ("stream", () => _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion })),
            ("summary", () => _s.UserA.Client.PostAsJsonAsync($"/api/v1/documents/{_s.PlansPdf}/summary", new { })),
            ("compare", () => _s.UserA.Client.PostAsJsonAsync("/api/v1/documents/compare", new { documentIdA = _s.PlansPdf, documentIdB = _s.HolidaysTxt })),
        };
        foreach (var (name, send) in calls)
        {
            var response = await send();

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: got {(int)response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await Json(response);
            Assert.Equal("llm_key_required", problem.GetProperty("code").GetString());
            Assert.Contains("/api/v1/me/llm-key", problem.GetProperty("detail").GetString());
        }

        Assert.Empty(_s.Llm.Calls);
        Assert.Empty(_s.Backend.Requests);

        // with a key the same user can ask
        await SaveKeyAsync(_s.UserA);
        _s.Backend.Chat = ScriptedModel();
        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);
    }

    [Fact]
    public async Task A_saved_key_that_cannot_be_decrypted_gives_409_asking_the_user_to_save_it_again()
    {
        await DocumentTestHelpers.AddCredentialAsync(_s.ConnectionString, _s.UserB.Id); // "enc" is not a valid Data Protection payload

        var response = await Ask(_s.UserB);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("llm_key_unreadable", (await Json(response)).GetProperty("code").GetString());
        Assert.Empty(_s.Backend.Requests);
    }

    [Fact]
    public async Task Successful_streaming_answers_carry_the_byok_route()
    {
        await SaveKeyAsync(_s.UserA);
        _s.Backend.Chat = ScriptedModel();

        var response = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: answer", text);
        Assert.Contains("\"provider\":\"OpenRouter\"", text);
        await SecretAssert.NotLeakedAsync(Key, response, _s.Logs, _s.Activities);
    }
}
