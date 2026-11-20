using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Llm;
using DocAgent.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocAgent.IntegrationTests;

/// <summary>A clock that tests move; everything else keeps working because only the quota logic looks at the day.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset? _fixed;

    public override DateTimeOffset GetUtcNow() => _fixed ?? DateTimeOffset.UtcNow;

    public void Set(DateTimeOffset value) => _fixed = value;

    public void Advance(TimeSpan by) => _fixed = GetUtcNow() + by;

    public void UseRealTime() => _fixed = null;
}

/// <summary>
/// The real route selection in demo mode (<c>Llm:NoKeyRoute=Demo</c>): no-key users go to OpenRouter on the operator's key, with the
/// provider HTTP mocked. The daily limit is 3 here to keep the tests fast (the default of 10 is covered separately).
/// </summary>
public sealed class DemoScenario(PostgresFixture postgres) : QaScenario(postgres)
{
    public const string OperatorKey = "sk-or-v1-OPERATORKEY-0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    public const int DailyLimit = 3;

    public override string Mode => "Agent";

    public ProviderBackend Backend { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();
    public ActivityCapture Activities { get; } = new();
    public ManualClock Clock { get; } = new();

    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>(SecretAssert.VerboseLogging)
    {
        ["Llm:NoKeyRoute"] = "Demo",
        ["Llm:Demo:OpenRouterApiKey"] = OperatorKey,
        ["Llm:Demo:DailyQuestionLimit"] = DailyLimit.ToString(),
        ["Agent:Limits:Demo:MaxToolCalls"] = "2",
        ["Agent:Limits:Byok:MaxToolCalls"] = "7",
    };

    protected override Action<IServiceCollection> ConfigureServices()
    {
        var register = Backend.Register(Logs);
        return services =>
        {
            register(services);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        };
    }

    /// <summary>A second API instance on the same database, as after an application restart.</summary>
    public DocAgentApiFactory NewApiInstance() => new(
        ConnectionString,
        new Dictionary<string, string?> { ["Documents:Ingestion:WorkerEnabled"] = "false", ["Qa:Mode"] = Mode }
            .Concat(ExtraSettings).ToDictionary(kv => kv.Key, kv => kv.Value),
        configureServices: ConfigureServices());

    public new async Task DisposeAsync()
    {
        Activities.Dispose();
        await base.DisposeAsync();
    }
}

[Collection(PostgresCollection.Name)]
public sealed class DemoModeTests : IClassFixture<DemoScenario>, IAsyncLifetime
{
    private const string Primary = DemoOptions.DefaultModel;
    private const string Fallback = DemoOptions.DefaultFallbackModel;
    private const string UserKey = "sk-or-v1-USERKEY-77665544332211ffeeddccbbaa998877";

    private readonly DemoScenario _s;

    public DemoModeTests(DemoScenario scenario)
    {
        _s = scenario;
        _s.Backend.Reset();
        _s.Clock.UseRealTime();
        _s.Backend.Chat = Scripted();
    }

    public async Task InitializeAsync()
    {
        await using var db = DocumentTestHelpers.OpenDb(_s.ConnectionString);
        await db.DemoUsages.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.UserLlmCredentials.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string ModelOf(StubTransport.RecordedRequest r) => JsonDocument.Parse(r.Body).RootElement.GetProperty("model").GetString()!;

    /// <summary>The agent flow on OpenRouter's wire format: search, answer, verify. <paramref name="fail"/> replaces the reply for chosen models.</summary>
    private static Func<StubTransport.RecordedRequest, HttpResponseMessage> Scripted(Func<string, HttpResponseMessage?>? fail = null) => request =>
    {
        if (fail?.Invoke(ModelOf(request)) is { } failure)
        {
            return failure;
        }

        if (request.Body.Contains("strict fact checker"))
        {
            return StubTransport.Json(ProviderJson.Text(LlmProvider.OpenRouter, """{"claims":[{"id":1,"supported":true,"source":1}],"answersQuestion":true}"""));
        }

        return StubTransport.Json(request.Body.Contains("\"role\":\"tool\"")
            ? ProviderJson.Text(LlmProvider.OpenRouter, "The Pro plan costs 20 dollars per month [1].")
            : ProviderJson.ToolCall(LlmProvider.OpenRouter, "search_documents", $$"""{"query":"{{QaScenario.PricingQuestion}}"}"""));
    };

    private static Task<HttpResponseMessage> Ask(TestUser user) =>
        user.Client.PostAsJsonAsync("/api/v1/ask", new { question = QaScenario.PricingQuestion });

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> MeAsync(TestUser user) => await user.Client.GetFromJsonAsync<JsonElement>("/api/v1/me");

    private async Task<int> StoredCountAsync(Guid userId)
    {
        await using var db = DocumentTestHelpers.OpenDb(_s.ConnectionString);
        return await db.DemoUsages.IgnoreQueryFilters().Where(u => u.UserId == userId).SumAsync(u => u.Count);
    }

    // ---- routing, limits, privacy ----

    [Fact]
    public async Task A_user_without_a_key_is_served_by_the_demo_model_on_the_operators_key()
    {
        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("The Pro plan costs 20 dollars per month [1].", body.GetProperty("answer").GetString());
        Assert.Equal("demo", body.GetProperty("mode").GetString());
        Assert.Equal("OpenRouter", body.GetProperty("provider").GetString());
        Assert.Equal(Primary, body.GetProperty("model").GetString());
        Assert.Equal(1, body.GetProperty("toolCalls").GetInt32());

        var calls = _s.Backend.Requests;
        Assert.Equal(3, calls.Count); // tool request, answer, verification
        Assert.All(calls, c => Assert.Equal("openrouter.ai", c.Url.Host));
        Assert.All(calls, c => Assert.Equal(Primary, ModelOf(c)));
        Assert.All(calls, c => Assert.Equal("Bearer " + DemoScenario.OperatorKey, c.Headers["authorization"]));
        Assert.Empty(_s.Llm.Calls);
        await SecretAssert.NotLeakedAsync(DemoScenario.OperatorKey, response, _s.Logs, _s.Activities);
    }

    [Fact]
    public async Task Demo_answers_carry_the_privacy_notice_first_and_keep_the_extraction_notices()
    {
        var body = await Json(await Ask(_s.UserA));

        var notices = body.GetProperty("notices").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Equal(DemoNoKeyRouteProvider.PrivacyNotice, notices[0]);
        Assert.Contains("third-party", notices[0]);
        Assert.Contains("OpenRouter", notices[0]);
        Assert.Contains("log", notices[0]);
    }

    [Fact]
    public async Task The_demo_route_uses_the_reduced_demo_agent_limits()
    {
        var toolOffers = 0;
        _s.Backend.Chat = request =>
        {
            if (request.Body.Contains("strict fact checker"))
            {
                return StubTransport.Json(ProviderJson.Text(LlmProvider.OpenRouter, """{"claims":[{"id":1,"supported":true,"source":1}],"answersQuestion":true}"""));
            }

            return request.Body.Contains("\"tools\"")
                ? StubTransport.Json(ProviderJson.ToolCall(LlmProvider.OpenRouter, "search_documents", $$"""{"query":"pro plan {{Interlocked.Increment(ref toolOffers)}}"}"""))
                : StubTransport.Json(ProviderJson.Text(LlmProvider.OpenRouter, "The Pro plan costs 20 dollars per month [1]."));
        };

        var body = await Json(await Ask(_s.UserA));

        Assert.Equal(2, body.GetProperty("toolCalls").GetInt32()); // Agent:Limits:Demo:MaxToolCalls in this scenario (not 7 byok, not 9 development)
        Assert.True(body.GetProperty("incomplete").GetBoolean());
        Assert.Equal("demo", body.GetProperty("mode").GetString());
    }

    [Fact]
    public void The_default_daily_limit_is_ten_and_the_default_models_are_the_chosen_free_gemma_models()
    {
        var options = new DemoOptions();
        Assert.Equal(10, options.DailyQuestionLimit);
        Assert.Equal("google/gemma-4-31b-it:free", options.Model);
        Assert.Equal("google/gemma-4-26b-a4b-it:free", options.FallbackModel);
        Assert.Null(options.OpenRouterApiKey);
    }

    // ---- quota ----

    [Fact]
    public async Task The_daily_allowance_is_counted_reported_by_me_and_refused_with_a_clear_429_when_used_up()
    {
        var me = (await MeAsync(_s.UserA)).GetProperty("demo");
        Assert.Equal(DemoScenario.DailyLimit, me.GetProperty("limit").GetInt32());
        Assert.Equal(0, me.GetProperty("used").GetInt32());
        Assert.Equal(DemoScenario.DailyLimit, me.GetProperty("remaining").GetInt32());
        Assert.Contains("third-party", me.GetProperty("privacyNotice").GetString());
        var resetsAt = me.GetProperty("resetsAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, resetsAt.TimeOfDay);
        Assert.Equal(DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(1), resetsAt.UtcDateTime);

        for (var i = 0; i < DemoScenario.DailyLimit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);
        }

        me = (await MeAsync(_s.UserA)).GetProperty("demo");
        Assert.Equal(DemoScenario.DailyLimit, me.GetProperty("used").GetInt32());
        Assert.Equal(0, me.GetProperty("remaining").GetInt32());

        _s.Backend.Reset();
        var refused = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var problem = await Json(refused);
        Assert.Equal("demo_quota_exceeded", problem.GetProperty("code").GetString());
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal(DemoScenario.DailyLimit, problem.GetProperty("limit").GetInt32());
        Assert.Equal(resetsAt, problem.GetProperty("resetsAt").GetDateTimeOffset());
        Assert.Contains("add your own LLM API key", problem.GetProperty("detail").GetString());
        var retryAfter = int.Parse(refused.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, 86400);
        Assert.Empty(_s.Backend.Requests); // refused before any model call
    }

    [Fact]
    public async Task Ask_stream_summary_and_compare_each_cost_one_question()
    {
        var plans = _s.PlansPdf;
        var holidays = _s.HolidaysTxt;

        var ask = await Ask(_s.UserA);
        var stream = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion });
        var streamText = await stream.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        Assert.Contains("event: answer", streamText);
        Assert.Contains("\"mode\":\"demo\"", streamText);
        Assert.Contains("third-party", streamText); // the privacy notice travels in the streamed answer too
        Assert.Equal(2, await StoredCountAsync(_s.UserA.Id));

        _s.Backend.Chat = _ => StubTransport.Json(ProviderJson.Text(LlmProvider.OpenRouter, "- a point [1]"));
        var summary = await _s.UserA.Client.PostAsJsonAsync($"/api/v1/documents/{plans}/summary", new { });
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal("demo", (await Json(summary)).GetProperty("mode").GetString());
        Assert.Equal(3, await StoredCountAsync(_s.UserA.Id));

        var compare = await _s.UserA.Client.PostAsJsonAsync("/api/v1/documents/compare", new { documentIdA = plans, documentIdB = holidays });
        Assert.Equal(HttpStatusCode.TooManyRequests, compare.StatusCode); // the allowance of 3 is used up: the comparison is refused
        Assert.Equal("demo_quota_exceeded", (await Json(compare)).GetProperty("code").GetString());
        Assert.Equal(3, await StoredCountAsync(_s.UserA.Id));

        // A new day: compare is one question, and so is a refused-then-retried summary of the same user.
        await using (var db = DocumentTestHelpers.OpenDb(_s.ConnectionString))
        {
            await db.DemoUsages.IgnoreQueryFilters().ExecuteDeleteAsync();
        }

        var compared = await _s.UserA.Client.PostAsJsonAsync("/api/v1/documents/compare", new { documentIdA = plans, documentIdB = holidays });
        Assert.Equal(HttpStatusCode.OK, compared.StatusCode);
        Assert.Equal(1, await StoredCountAsync(_s.UserA.Id));
    }

    [Fact]
    public async Task Requests_that_are_rejected_before_any_model_call_do_not_cost_a_question()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask", new { question = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Ask(_s.EmptyUser)).StatusCode); // no ready documents
        Assert.Equal(HttpStatusCode.NotFound, (await _s.UserA.Client.PostAsJsonAsync($"/api/v1/documents/{_s.UserBPrices}/summary", new { })).StatusCode);

        Assert.Equal(0, await StoredCountAsync(_s.UserA.Id));
        Assert.Equal(0, await StoredCountAsync(_s.EmptyUser.Id));
    }

    [Fact]
    public async Task Parallel_requests_can_never_use_more_than_the_allowance()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Ask(_s.UserB)));

        Assert.Equal(DemoScenario.DailyLimit, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(12 - DemoScenario.DailyLimit, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(DemoScenario.DailyLimit, await StoredCountAsync(_s.UserB.Id));
    }

    [Fact]
    public async Task The_allowance_resets_at_the_utc_midnight()
    {
        var yesterday = DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(-1);
        _s.Clock.Set(new DateTimeOffset(yesterday.AddHours(23).AddMinutes(59).AddSeconds(30), TimeSpan.Zero));
        for (var i = 0; i < DemoScenario.DailyLimit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);
        }

        var refused = await Ask(_s.UserA);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(30, int.Parse(refused.Headers.GetValues("Retry-After").Single())); // 30 s to the boundary

        _s.Clock.Advance(TimeSpan.FromSeconds(31)); // 00:00:01 of the next UTC day

        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);
        var me = (await MeAsync(_s.UserA)).GetProperty("demo");
        Assert.Equal(1, me.GetProperty("used").GetInt32());
        Assert.Equal(yesterday.AddDays(2), me.GetProperty("resetsAt").GetDateTimeOffset().UtcDateTime);
    }

    [Fact]
    public async Task The_allowance_survives_an_application_restart()
    {
        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);

        await using var restarted = _s.NewApiInstance();
        using var anonymous = restarted.CreateClient();
        var tokens = await anonymous.LoginAsync(_s.UserA.Email);
        using var client = restarted.CreateClient().WithBearer(tokens.AccessToken);

        var me = (await client.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("demo");
        Assert.Equal(2, me.GetProperty("used").GetInt32());
        Assert.Equal(1, me.GetProperty("remaining").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/ask", new { question = QaScenario.PricingQuestion })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/ask", new { question = QaScenario.PricingQuestion })).StatusCode);
    }

    [Fact]
    public async Task Allowances_are_per_user_and_removed_with_the_account()
    {
        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserB)).StatusCode);
        Assert.Equal(1, await StoredCountAsync(_s.UserA.Id));
        Assert.Equal(1, await StoredCountAsync(_s.UserB.Id));
        Assert.Equal(1, (await MeAsync(_s.UserB)).GetProperty("demo").GetProperty("used").GetInt32());

        var doomed = await DocumentTestHelpers.CreateUserAsync(_s.Factory);
        await using (var db = TestDatabase.CreateContext(_s.ConnectionString, doomed.Id))
        {
            db.DemoUsages.Add(new DemoUsage { UserId = doomed.Id, Day = DateOnly.FromDateTime(DateTime.UtcNow), Count = 2 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, await StoredCountAsync(doomed.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await doomed.Client.DeleteAsync("/api/v1/me")).StatusCode);
        Assert.Equal(0, await StoredCountAsync(doomed.Id));
        Assert.Equal(1, await StoredCountAsync(_s.UserA.Id)); // others untouched
    }

    [Fact]
    public async Task The_query_filter_hides_other_users_usage_rows()
    {
        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);

        await using var asB = TestDatabase.CreateContext(_s.ConnectionString, _s.UserB.Id);
        Assert.Empty(await asB.DemoUsages.ToListAsync());
        await using var anonymous = TestDatabase.CreateContext(_s.ConnectionString, null);
        Assert.Empty(await anonymous.DemoUsages.ToListAsync());
        await using var asA = TestDatabase.CreateContext(_s.ConnectionString, _s.UserA.Id);
        Assert.Single(await asA.DemoUsages.ToListAsync());
    }

    // ---- fallback and failures ----

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task When_the_primary_model_is_rate_limited_or_down_the_fallback_answers_and_is_reported(HttpStatusCode status)
    {
        _s.Backend.Chat = Scripted(model => model == Primary ? ProviderBackend.Error(status) : null);

        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(Fallback, body.GetProperty("model").GetString());
        Assert.Equal("demo", body.GetProperty("mode").GetString());
        Assert.Equal("OpenRouter", body.GetProperty("provider").GetString());
        Assert.Equal(Primary, ModelOf(_s.Backend.Requests[0]));
        Assert.All(_s.Backend.Requests.Skip(1), r => Assert.Equal(Fallback, ModelOf(r))); // one retry, then the request stays on the fallback
        Assert.Equal(1, await StoredCountAsync(_s.UserA.Id)); // answered: it counts
    }

    [Fact]
    public async Task When_both_models_are_down_the_answer_is_503_demo_unavailable_and_the_question_is_given_back()
    {
        _s.Backend.Chat = _ => ProviderBackend.Error(HttpStatusCode.ServiceUnavailable, "{\"error\":\"" + DemoScenario.OperatorKey + "\"}");

        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await Json(response);
        Assert.Equal("demo_unavailable", problem.GetProperty("code").GetString());
        Assert.Contains("add your own LLM API key", problem.GetProperty("detail").GetString());
        Assert.Equal(2, _s.Backend.Requests.Count); // primary, fallback
        Assert.Equal(0, await StoredCountAsync(_s.UserA.Id));
        Assert.Equal(DemoScenario.DailyLimit, (await MeAsync(_s.UserA)).GetProperty("demo").GetProperty("remaining").GetInt32());
        await SecretAssert.NotLeakedAsync(DemoScenario.OperatorKey, response, _s.Logs, _s.Activities);
    }

    [Fact]
    public async Task When_both_models_are_rate_limited_the_answer_is_429_with_retry_after()
    {
        _s.Backend.Chat = _ => ProviderBackend.Error(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(17));

        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("demo_rate_limited", (await Json(response)).GetProperty("code").GetString());
        Assert.Equal("17", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(0, await StoredCountAsync(_s.UserA.Id));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public async Task An_invalid_operator_key_is_503_demo_unavailable_logged_as_an_error_without_the_key_and_without_fallback(HttpStatusCode status)
    {
        _s.Backend.Chat = _ => ProviderBackend.Error(status, "{\"error\":{\"message\":\"Invalid key " + DemoScenario.OperatorKey + "\"}}");

        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await Json(response);
        Assert.Equal("demo_unavailable", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("operator", problem.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase); // a generic message: no operator detail for the user
        Assert.Single(_s.Backend.Requests);
        Assert.Contains(_s.Logs.Lines, l => l.Contains("DemoChatClient Error") && l.Contains("Llm:Demo:OpenRouterApiKey"));
        Assert.Equal(0, await StoredCountAsync(_s.UserA.Id));
        await SecretAssert.NotLeakedAsync(DemoScenario.OperatorKey, response, _s.Logs, _s.Activities);
    }

    [Fact]
    public async Task The_streamed_answer_reports_demo_failures_as_an_error_event()
    {
        _s.Backend.Chat = _ => ProviderBackend.Error(HttpStatusCode.ServiceUnavailable);

        var response = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: error", text);
        Assert.Contains("demo_unavailable", text);
        Assert.Equal(0, await StoredCountAsync(_s.UserA.Id));
    }

    // ---- BYOK bypass (KEY-6) ----

    [Fact]
    public async Task A_user_with_a_saved_key_never_uses_the_demo_route_or_its_allowance()
    {
        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode); // one demo question first
        var put = await _s.UserA.Client.PutAsJsonAsync("/api/v1/me/llm-key", new { provider = "openrouter", model = "openai/gpt-4o-mini", apiKey = UserKey });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        _s.Backend.Reset();
        _s.Backend.Chat = Scripted();

        for (var i = 0; i < DemoScenario.DailyLimit + 2; i++) // more than the demo allowance
        {
            var response = await Ask(_s.UserA);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await Json(response);
            Assert.Equal("byok", body.GetProperty("mode").GetString());
            Assert.Equal("openai/gpt-4o-mini", body.GetProperty("model").GetString());
            Assert.DoesNotContain(DemoNoKeyRouteProvider.PrivacyNotice, body.GetProperty("notices").EnumerateArray().Select(n => n.GetString()));
        }

        Assert.All(_s.Backend.Requests, r => Assert.Equal("Bearer " + UserKey, r.Headers["authorization"])); // never the operator's key
        Assert.Equal(1, await StoredCountAsync(_s.UserA.Id)); // unchanged since the demo question
        Assert.Equal(JsonValueKind.Null, (await MeAsync(_s.UserA)).GetProperty("demo").ValueKind);
    }

    [Fact]
    public async Task Removing_the_key_returns_the_user_to_the_demo_with_what_is_left_of_the_allowance()
    {
        await _s.UserA.Client.PutAsJsonAsync("/api/v1/me/llm-key", new { provider = "openrouter", model = "openai/gpt-4o-mini", apiKey = UserKey });
        _s.Backend.Reset();
        _s.Backend.Chat = Scripted();
        Assert.Equal("byok", (await Json(await Ask(_s.UserA))).GetProperty("mode").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _s.UserA.Client.DeleteAsync("/api/v1/me/llm-key")).StatusCode);

        Assert.Equal("demo", (await Json(await Ask(_s.UserA))).GetProperty("mode").GetString());
        Assert.Equal(DemoScenario.DailyLimit - 1, (await MeAsync(_s.UserA)).GetProperty("demo").GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task A_user_who_used_up_the_allowance_can_continue_by_adding_a_key()
    {
        for (var i = 0; i < DemoScenario.DailyLimit; i++)
        {
            await Ask(_s.UserA);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Ask(_s.UserA)).StatusCode);
        await _s.UserA.Client.PutAsJsonAsync("/api/v1/me/llm-key", new { provider = "openrouter", model = "openai/gpt-4o-mini", apiKey = UserKey });
        _s.Backend.Reset();
        _s.Backend.Chat = Scripted();

        Assert.Equal("byok", (await Json(await Ask(_s.UserA))).GetProperty("mode").GetString());
    }

    // ---- demo document limits (DOC-3): the demo tier applies to users without a key; see DocumentLimitsTests for the numbers ----

    [Fact]
    public async Task Demo_document_limits_apply_to_users_without_a_key_and_the_hint_suggests_a_key()
    {
        // UserA already owns three documents in this scenario (the demo maximum).
        var upload = await _s.UserA.UploadAsync("hello"u8.ToArray(), "extra.txt");

        Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        Assert.Contains("Add your own LLM API key", await upload.Content.ReadAsStringAsync());
    }
}
