using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using DocAgent.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.IntegrationTests;

/// <summary>Demo mode with the shared (all users) request limit at 2 per minute.</summary>
public sealed class ThrottledDemoScenario(PostgresFixture postgres) : DemoScenario(postgres)
{
    public const int PerMinute = 2;

    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>(base.ExtraSettings)
    {
        ["Llm:Demo:GlobalRequestsPerMinute"] = PerMinute.ToString(),
        ["Llm:Demo:DailyQuestionLimit"] = "10",
    };
}

[Collection(PostgresCollection.Name)]
public sealed class DemoGlobalThrottleEndpointTests : IClassFixture<ThrottledDemoScenario>
{
    private readonly ThrottledDemoScenario _s;

    public DemoGlobalThrottleEndpointTests(ThrottledDemoScenario scenario)
    {
        _s = scenario;
        _s.Backend.Reset();
        _s.Backend.Chat = request =>
        {
            if (request.Body.Contains("strict fact checker"))
            {
                return StubTransport.Json(ProviderJson.Text(LlmProvider.OpenRouter, """{"claims":[{"id":1,"supported":true,"source":1}],"answersQuestion":true}"""));
            }

            return StubTransport.Json(request.Body.Contains("\"role\":\"tool\"")
                ? ProviderJson.Text(LlmProvider.OpenRouter, "The Pro plan costs 20 dollars per month [1].")
                : ProviderJson.ToolCall(LlmProvider.OpenRouter, "search_documents", $$"""{"query":"{{QaScenario.PricingQuestion}}"}"""));
        };
    }

    [Fact]
    public async Task Demo_requests_over_the_shared_limit_get_429_demo_rate_limited_without_costing_a_question_or_a_provider_call()
    {
        // Two requests from two different users use up the shared allowance of this minute...
        Assert.Equal(HttpStatusCode.OK, (await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask", new { question = QaScenario.PricingQuestion })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _s.UserB.Client.PostAsJsonAsync("/api/v1/ask", new { question = "How much does the Pro plan cost per month?" })).StatusCode);
        var providerCalls = _s.Backend.Requests.Count;

        // ...so a third user is refused, whatever the endpoint.
        var refused = await _s.EmptyUser.Client.PostAsJsonAsync("/api/v1/ask", new { question = QaScenario.PricingQuestion });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode); // no documents: refused by validation before the route, so it never reaches the throttle

        var asA = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask", new { question = QaScenario.PricingQuestion });
        Assert.Equal(HttpStatusCode.TooManyRequests, asA.StatusCode);
        Assert.Equal("application/problem+json", asA.Content.Headers.ContentType?.MediaType);
        var body = await asA.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("demo_rate_limited", body.GetProperty("code").GetString());
        Assert.True(int.Parse(asA.Headers.GetValues("Retry-After").Single()) is > 0 and <= 60);

        var stream = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion });
        Assert.Equal(HttpStatusCode.TooManyRequests, stream.StatusCode);
        var summary = await _s.UserA.Client.PostAsJsonAsync($"/api/v1/documents/{_s.HolidaysTxt}/summary", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, summary.StatusCode);

        Assert.Equal(providerCalls, _s.Backend.Requests.Count); // nothing was sent to OpenRouter for the refused requests

        // The refused requests did not use up the user's daily questions: 1 asked, 9 left.
        var me = await _s.UserA.Client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(1, me.GetProperty("demo").GetProperty("used").GetInt32());

        // Everything that does not call the demo model is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await _s.UserA.Client.GetAsync("/api/v1/documents")).StatusCode);
    }
}
