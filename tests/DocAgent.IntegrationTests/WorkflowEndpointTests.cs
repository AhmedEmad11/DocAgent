using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static DocAgent.IntegrationTests.AgentScript;

namespace DocAgent.IntegrationTests;

/// <summary>Summary (QA-6) and comparison (QA-7) endpoints with a scripted model.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkflowEndpointTests : IClassFixture<AgentQaScenario>
{
    private readonly AgentQaScenario _s;

    public WorkflowEndpointTests(AgentQaScenario scenario)
    {
        _s = scenario;
        _s.Llm.Reset();
        _s.LlmFactory.Limits = null;
        _s.Llm.Handler = Workflow();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> Summary(TestUser user, Guid id, object? body = null) =>
        body is null ? user.Client.PostAsync($"/api/v1/documents/{id}/summary", null) : user.Client.PostAsJsonAsync($"/api/v1/documents/{id}/summary", body);

    private Task<HttpResponseMessage> Compare(TestUser user, Guid? a, Guid? b, string? focus = null) =>
        user.Client.PostAsJsonAsync("/api/v1/documents/compare", new { documentIdA = a, documentIdB = b, focus });

    [Fact]
    public async Task Summary_cites_the_chunks_of_the_document_by_page_and_the_body_is_optional()
    {
        var response = await Summary(_s.UserA, _s.PlansPdf);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.False(body.GetProperty("notFound").GetBoolean());
        Assert.False(body.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0, body.GetProperty("toolCalls").GetInt32());
        Assert.Equal("development", body.GetProperty("mode").GetString());
        var citations = body.GetProperty("citations").EnumerateArray().ToList();
        Assert.Equal([1, 2], citations.Select(c => c.GetProperty("index").GetInt32()));
        Assert.All(citations, c => Assert.Equal(_s.PlansPdf, c.GetProperty("documentId").GetGuid()));
        Assert.Equal([1, 2], citations.Select(c => c.GetProperty("pageNumber").GetInt32()));
        Assert.Contains("[1]", body.GetProperty("answer").GetString());
        Assert.Contains("Pro plan costs 20 dollars", citations[0].GetProperty("quote").GetString());
        var call = Assert.Single(_s.Llm.Calls);
        Assert.DoesNotContain("99 dollars", call.Messages[1].Text);
        Assert.Contains("untrusted", call.Messages[0].Text);
        Assert.Contains("English", call.Messages[0].Text);
    }

    [Fact]
    public async Task Summary_focus_reaches_the_prompt_and_is_validated()
    {
        var ok = await Summary(_s.UserA, _s.HolidaysTxt, new { focus = "vacation rules" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("vacation rules", _s.Llm.Calls[0].Messages[0].Text);

        Assert.Equal(HttpStatusCode.BadRequest, (await Summary(_s.UserA, _s.HolidaysTxt, new { focus = new string('x', 501) })).StatusCode);
    }

    [Fact]
    public async Task Summary_uses_the_language_of_the_focus_or_the_document()
    {
        await Summary(_s.UserA, _s.HolidaysTxt, new { focus = "الإجازات" });

        Assert.Contains("Write in Arabic", _s.Llm.Calls[0].Messages[0].Text);
    }

    [Fact]
    public async Task Summary_of_a_foreign_unknown_or_not_ready_document_is_404_or_409()
    {
        var foreign = await Summary(_s.UserA, _s.UserBPrices);
        var unknown = await Summary(_s.UserA, Guid.NewGuid());
        var notReady = await Summary(_s.UserA, _s.PendingDocument);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal((await Json(foreign)).GetProperty("detail").GetString(), (await Json(unknown)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);
        Assert.Empty(_s.Llm.Calls);
    }

    [Fact]
    public async Task Workflow_endpoints_need_a_confirmed_user()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Summary(_s.UnconfirmedUser, _s.PlansPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Compare(_s.UnconfirmedUser, _s.PlansPdf, _s.HolidaysTxt)).StatusCode);
        using var anonymous = _s.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/documents/{_s.PlansPdf}/summary", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/documents/compare", new { })).StatusCode);
    }

    [Fact]
    public async Task Summary_includes_the_extraction_notice_of_the_document()
    {
        await using var db = DocumentTestHelpers.OpenDb(_s.ConnectionString);
        await db.Documents.IgnoreQueryFilters().Where(d => d.Id == _s.PlansPdf).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExtractionNotice, "Arabic text may be inaccurate."));
        try
        {
            var body = await Json(await Summary(_s.UserA, _s.PlansPdf));

            Assert.Equal("plans.pdf: Arabic text may be inaccurate.", Assert.Single(body.GetProperty("notices").EnumerateArray().ToList()).GetString());
            var clean = await Json(await Summary(_s.UserA, _s.HolidaysTxt));
            Assert.Equal(0, clean.GetProperty("notices").GetArrayLength());
        }
        finally
        {
            await db.Documents.IgnoreQueryFilters().Where(d => d.Id == _s.PlansPdf).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExtractionNotice, (string?)null));
        }
    }

    [Fact]
    public async Task Compare_cites_both_documents()
    {
        var response = await Compare(_s.UserA, _s.PlansPdf, _s.HolidaysTxt, "benefits");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        var cited = body.GetProperty("citations").EnumerateArray().Select(c => c.GetProperty("documentId").GetGuid()).Distinct().ToList();
        Assert.Contains(_s.PlansPdf, cited);
        Assert.Contains(_s.HolidaysTxt, cited);
        Assert.Contains("Topic: A", body.GetProperty("answer").GetString());
        Assert.Contains("Topic: B", body.GetProperty("answer").GetString());
        Assert.Contains(_s.Llm.Calls, c => c.Messages[0].Text.Contains("You compare two documents") && c.Messages[0].Text.Contains("benefits"));
        Assert.DoesNotContain("99 dollars", string.Concat(_s.Llm.Calls.Select(c => c.Messages[1].Text)));
    }

    [Fact]
    public async Task Compare_validation_and_tenant_scoping()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Compare(_s.UserA, null, _s.PlansPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Compare(_s.UserA, _s.PlansPdf, _s.PlansPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Compare(_s.UserA, _s.PlansPdf, _s.HolidaysTxt, new string('x', 501))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Compare(_s.UserA, _s.PlansPdf, _s.UserBPrices)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Compare(_s.UserA, Guid.NewGuid(), _s.PlansPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Compare(_s.UserA, _s.PlansPdf, _s.PendingDocument)).StatusCode);
        Assert.Empty(_s.Llm.Calls);
    }

    [Fact]
    public async Task Compare_notices_come_from_both_documents()
    {
        await using var db = DocumentTestHelpers.OpenDb(_s.ConnectionString);
        await db.Documents.IgnoreQueryFilters().Where(d => d.Id == _s.HolidaysTxt).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExtractionNotice, "Best-effort extraction."));
        try
        {
            var body = await Json(await Compare(_s.UserA, _s.PlansPdf, _s.HolidaysTxt));

            Assert.Equal("holidays.txt: Best-effort extraction.", Assert.Single(body.GetProperty("notices").EnumerateArray().ToList()).GetString());
        }
        finally
        {
            await db.Documents.IgnoreQueryFilters().Where(d => d.Id == _s.HolidaysTxt).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExtractionNotice, (string?)null));
        }
    }

    [Fact]
    public async Task A_timeout_during_the_reduce_step_returns_the_partial_summary_marked_incomplete()
    {
        var agent = _s.Factory.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
        var originalBudget = agent.MapBudgetTokens;
        agent.MapBudgetTokens = 20; // one chunk per map call, so there is a reduce step
        _s.LlmFactory.Limits = new AgentLimits(10, TimeSpan.FromSeconds(3));
        try
        {
            _s.Llm.Handler = Workflow(async (call, ct) =>
            {
                if (call.Messages[0].Text.Contains("You merge partial summaries"))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct); // never finishes in time; the deadline ends it
                }
            });

            var started = DateTime.UtcNow;
            var response = await Summary(_s.UserA, _s.PlansPdf);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
            var body = await Json(response);
            Assert.True(body.GetProperty("incomplete").GetBoolean());
            Assert.Contains("may be incomplete", body.GetProperty("answer").GetString());
            Assert.True(body.GetProperty("citations").GetArrayLength() >= 1);
            Assert.Contains(_s.Llm.Calls, c => c.Messages[0].Text.Contains("You merge partial summaries"));
        }
        finally
        {
            agent.MapBudgetTokens = originalBudget;
        }
    }

    [Fact]
    public async Task Unreachable_model_gives_503()
    {
        _s.Llm.Handler = (_, _) => throw new HttpRequestException("down");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Summary(_s.UserA, _s.PlansPdf)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Compare(_s.UserA, _s.PlansPdf, _s.HolidaysTxt)).StatusCode);
    }

    [Fact]
    public async Task The_agent_can_call_the_summary_workflow_as_a_tool_and_cite_its_markers()
    {
        _s.Llm.Handler = async (call, ct) =>
        {
            if (IsVerifier(call))
            {
                return Verdicts(call);
            }

            if (IsAgent(call))
            {
                return ToolResults(call).Count == 0
                    ? ToolCall("summarize_document", Args(("documentId", "plans.pdf")))
                    : Text("The plans document describes pricing and support [1][2].");
            }

            return await Workflow()(call, ct);
        };

        var body = await Json(await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask", new { question = "Give me an overview of plans.pdf" }));

        // The verifier (a separate call) sees the cited chunks themselves, not the summary.
        Assert.Equal(1, body.GetProperty("toolCalls").GetInt32());
        Assert.Contains(_s.Llm.Calls, c => !IsAgent(c) && c.Messages[0].Text.Contains("You condense a document"));
        Assert.Equal(2, body.GetProperty("citations").GetArrayLength());
    }
}
