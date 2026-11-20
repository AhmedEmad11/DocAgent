using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DocAgent.IntegrationTests;

/// <summary>Per-user concurrency cap on the model-backed endpoints, with the real documents and the scripted model of <see cref="QaScenario"/>.</summary>
public sealed class HardeningScenario(PostgresFixture postgres) : QaScenario(postgres)
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["RateLimiting:Ask:MaxConcurrentPerUser"] = "1",
    };
}

[Collection(PostgresCollection.Name)]
public sealed class HardeningScenarioTests : IClassFixture<HardeningScenario>
{
    private const string SecretDetail = "SECRET-INTERNAL-DETAIL /srv/app/Secrets.cs";
    private readonly HardeningScenario _s;

    public HardeningScenarioTests(HardeningScenario scenario)
    {
        _s = scenario;
        _s.Llm.Reset();
    }

    private static Task<HttpResponseMessage> Ask(TestUser user) =>
        user.Client.PostAsJsonAsync("/api/v1/ask", new { question = QaScenario.PricingQuestion });

    [Fact]
    public async Task A_second_question_while_one_is_running_is_refused_with_429_and_the_slot_frees_up_afterwards()
    {
        var gate = new TaskCompletionSource();
        _s.Llm.Handler = async (_, _) =>
        {
            await gate.Task;
            return FakeChatClient.Reply("The Pro plan costs 20 dollars per month [1].", false);
        };

        var first = Ask(_s.UserA);
        await WaitUntilAsync(() => _s.Llm.Calls.Count == 1);

        var second = await Ask(_s.UserA);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        Assert.True(second.Headers.Contains("Retry-After"));

        // The same budget covers summary and compare, and streaming.
        var summary = await _s.UserA.Client.PostAsJsonAsync($"/api/v1/documents/{_s.HolidaysTxt}/summary", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, summary.StatusCode);
        var stream = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion });
        Assert.Equal(HttpStatusCode.TooManyRequests, stream.StatusCode);
        Assert.Single(_s.Llm.Calls); // none of the refused requests reached the model

        // Another user has their own slot (their question is blocked at the model like the first, so only check it was admitted).
        var otherUser = Ask(_s.UserB);
        await WaitUntilAsync(() => _s.Llm.Calls.Count == 2);

        gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherUser).StatusCode);

        _s.Llm.Reset();
        Assert.Equal(HttpStatusCode.OK, (await Ask(_s.UserA)).StatusCode);
    }

    [Fact]
    public async Task An_unexpected_failure_returns_a_generic_500_problem_without_exception_details()
    {
        _s.Llm.Handler = (_, _) => throw new InvalidOperationException(SecretDetail);

        var response = await Ask(_s.UserA);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-INTERNAL-DETAIL", text);
        Assert.DoesNotContain("InvalidOperationException", text);
        Assert.DoesNotContain("Secrets.cs", text);
        Assert.DoesNotContain(" at DocAgent", text);
        Assert.DoesNotContain("StackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(500, JsonDocument.Parse(text).RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_failure_inside_the_stream_is_a_fixed_error_event_without_details()
    {
        _s.Llm.Handler = (_, _) => throw new InvalidOperationException(SecretDetail);

        var response = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion });
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: error", text);
        Assert.DoesNotContain("SECRET-INTERNAL-DETAIL", text);
        Assert.DoesNotContain("InvalidOperationException", text);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the model call");
            await Task.Delay(25);
        }
    }
}
