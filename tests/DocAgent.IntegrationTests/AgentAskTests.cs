using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Infrastructure.Agents;
using DocAgent.Infrastructure.Llm;
using DocAgent.Infrastructure.Qa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static DocAgent.IntegrationTests.AgentScript;

namespace DocAgent.IntegrationTests;

/// <summary>/ask in Agent mode with a scripted chat client: it issues tool calls, then answers; a separate call verifies.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentAskTests : IClassFixture<AgentQaScenario>
{
    private readonly AgentQaScenario _s;

    public AgentAskTests(AgentQaScenario scenario)
    {
        _s = scenario;
        _s.Llm.Reset();
        _s.LlmFactory.Limits = null;
    }

    private static Task<HttpResponseMessage> Ask(TestUser user, string? question, params Guid[] documentIds) =>
        user.Client.PostAsJsonAsync("/api/v1/ask", new { question, documentIds = documentIds.Length == 0 ? null : documentIds });

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static Dictionary<string, object?> Search(string query) => Args(("query", query));

    [Fact]
    public async Task Agent_searches_answers_and_the_citations_map_back_to_document_page_and_quote()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCall("search_documents", Search(QaScenario.PricingQuestion))
            : Text("The Pro plan costs 20 dollars per month [1]."));

        var response = await Ask(_s.UserA, QaScenario.PricingQuestion);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("The Pro plan costs 20 dollars per month [1].", body.GetProperty("answer").GetString());
        Assert.False(body.GetProperty("notFound").GetBoolean());
        Assert.False(body.GetProperty("incomplete").GetBoolean());
        Assert.Equal(1, body.GetProperty("toolCalls").GetInt32());
        Assert.Equal("development", body.GetProperty("mode").GetString());
        Assert.Equal(0, body.GetProperty("notices").GetArrayLength());
        var citation = Assert.Single(body.GetProperty("citations").EnumerateArray().ToList());
        Assert.Equal(1, citation.GetProperty("index").GetInt32());
        Assert.Equal(_s.PlansPdf, citation.GetProperty("documentId").GetGuid());
        Assert.Equal("plans.pdf", citation.GetProperty("fileName").GetString());
        Assert.Equal(1, citation.GetProperty("pageNumber").GetInt32());
        Assert.Contains("Pro plan costs 20 dollars", citation.GetProperty("quote").GetString());

        var calls = _s.Llm.Calls;
        Assert.Equal(3, calls.Count); // tool request, answer, verification
        var first = calls[0];
        Assert.Contains("untrusted data", first.Options!.Instructions);
        Assert.Contains("same language as the question", first.Options.Instructions);
        Assert.Equal(
            ["compare_documents", "get_chunk_context", "get_document_outline", "search_documents", "summarize_document"],
            first.Options.Tools!.Select(t => t.Name).Order());
        Assert.Contains("Question: " + QaScenario.PricingQuestion, first.Messages.Last(m => m.Role == ChatRole.User).Text);
        Assert.Contains("plans.pdf", first.Messages.Last(m => m.Role == ChatRole.User).Text);
        var toolResult = Assert.Single(ToolResults(calls[1]));
        Assert.Contains("<source index=\"1\" documentId=\"" + _s.PlansPdf + "\" file=\"plans.pdf\" page=\"1\"", toolResult);
        Assert.DoesNotContain("99 dollars", toolResult); // the other user's document never reaches the model
        Assert.True(IsVerifier(calls[2]));
        Assert.NotNull(calls[2].Options!.ResponseFormat);
    }

    [Fact]
    public async Task A_chunk_found_by_two_searches_keeps_one_index_and_is_not_sent_twice()
    {
        _s.Llm.Handler = Agent((turn, _) => turn switch
        {
            0 => ToolCall("search_documents", Search(QaScenario.PricingQuestion), "a"),
            1 => ToolCall("search_documents", Search("Pro plan and Basic plan monthly price"), "b"),
            _ => Text("The Pro plan costs 20 dollars per month [1]."),
        });

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.Equal(2, body.GetProperty("toolCalls").GetInt32());
        var results = ToolResults(_s.Llm.Calls[2]);
        Assert.Equal(2, results.Count);
        Assert.Contains("<source index=\"1\" documentId=", results[0]);
        Assert.Contains("<source index=\"1\" note=\"already shown above\" />", results[1]);
        Assert.Equal(1, body.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task Reaching_the_tool_call_cap_stops_tool_use_and_the_answer_says_it_may_be_incomplete()
    {
        _s.LlmFactory.Limits = new AgentLimits(2, TimeSpan.FromSeconds(60));
        var n = 0;
        _s.Llm.Handler = Agent((_, call) => call.Options?.Tools is { Count: > 0 }
            ? ToolCall("search_documents", Search($"pro plan price attempt {n++}"), $"c{n}")
            : Text("The Pro plan costs 20 dollars per month [1]."));

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.Equal(2, body.GetProperty("toolCalls").GetInt32());
        Assert.True(body.GetProperty("incomplete").GetBoolean());
        Assert.False(body.GetProperty("notFound").GetBoolean());
        Assert.EndsWith("Note: this answer may be incomplete because the search limit was reached.", body.GetProperty("answer").GetString());
        var agentCalls = _s.Llm.Calls.Where(IsAgent).ToList();
        Assert.Equal(3, agentCalls.Count);
        var last = agentCalls[^1];
        Assert.Null(last.Options!.Tools);
        Assert.Equal(ToolLimitChatClient.LimitNotice, last.Messages[^1].Text);
    }

    [Fact]
    public async Task Tool_calls_requested_together_beyond_the_cap_are_refused()
    {
        _s.LlmFactory.Limits = new AgentLimits(2, TimeSpan.FromSeconds(60));
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCalls(("search_documents", Search("pro plan price")), ("search_documents", Search("basic plan price")), ("search_documents", Search("support hours")))
            : Text("The Pro plan costs 20 dollars per month [1]."));

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.Equal(2, body.GetProperty("toolCalls").GetInt32());
        Assert.True(body.GetProperty("incomplete").GetBoolean());
        var results = ToolResults(_s.Llm.Calls.First(c => ToolResults(c).Count > 0 && IsAgent(c)));
        Assert.Equal(3, results.Count);
        Assert.Equal(CountedFunction_CapMessage, results[2]);
    }

    private const string CountedFunction_CapMessage =
        "Tool call refused: the tool-call limit is reached. Do not call any more tools; answer with the sources you already have, or reply NOT_FOUND.";

    [Fact]
    public async Task Unsupported_claims_are_removed_by_the_verification_pass()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0
                ? ToolCall("search_documents", Search(QaScenario.PricingQuestion))
                : Text("The Pro plan costs 20 dollars per month [1]. It also includes a free phone [1]."),
            unsupportedMarkers: "free phone");

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.Equal("The Pro plan costs 20 dollars per month [1].", body.GetProperty("answer").GetString());
        Assert.False(body.GetProperty("notFound").GetBoolean());
        Assert.Equal(1, body.GetProperty("citations").GetArrayLength());
        Assert.Contains("free phone", _s.Llm.Calls.Last(IsVerifier).Messages[1].Text);
    }

    [Fact]
    public async Task When_no_claim_survives_verification_the_answer_is_not_found_with_no_citations()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0
                ? ToolCall("search_documents", Search(QaScenario.PricingQuestion))
                : Text("The Pro plan costs 500 dollars [1]."),
            unsupportedMarkers: "500");

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.True(body.GetProperty("notFound").GetBoolean());
        Assert.Equal(QaService.NotFoundMessageEnglish, body.GetProperty("answer").GetString());
        Assert.Equal(0, body.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task When_the_verifier_gives_no_usable_reply_the_answer_is_withheld_as_not_found()
    {
        var turn = 0;
        _s.Llm.Handler = (call, _) => Task.FromResult(IsVerifier(call)
            ? Text("looks fine to me")
            : Interlocked.Increment(ref turn) == 1 ? ToolCall("search_documents", Search(QaScenario.PricingQuestion)) : Text("The Pro plan costs 20 dollars per month [1]."));

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.Equal(2, _s.Llm.Calls.Count(IsVerifier)); // one retry, then fail closed
        Assert.True(body.GetProperty("notFound").GetBoolean());
        Assert.Equal(0, body.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task The_agent_replying_NOT_FOUND_gives_not_found_in_the_questions_language_without_verification()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0 ? ToolCall("search_documents", Search("سعر")) : Text("NOT_FOUND"));

        var body = await Json(await Ask(_s.UserA, "ما هو سعر الخطة الشهرية؟"));

        Assert.True(body.GetProperty("notFound").GetBoolean());
        Assert.Equal(QaService.NotFoundMessageArabic, body.GetProperty("answer").GetString());
        Assert.Equal(0, body.GetProperty("citations").GetArrayLength());
        Assert.DoesNotContain(_s.Llm.Calls, IsVerifier);
    }

    [Fact]
    public async Task An_answer_without_any_retrieved_source_is_never_accepted()
    {
        // Nothing relevant in the documents: the search is empty, the (hallucinating) model answers anyway.
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCall("search_documents", Search("Who won the football World Cup in 2018"))
            : Text("France won [1]."));

        var body = await Json(await Ask(_s.UserA, "Who won the football World Cup in 2018?"));

        Assert.True(body.GetProperty("notFound").GetBoolean());
        Assert.Equal(0, body.GetProperty("citations").GetArrayLength());
        Assert.DoesNotContain(_s.Llm.Calls, IsVerifier);
        Assert.Contains("No passage matched", ToolResults(_s.Llm.Calls[1]).Single());
    }

    [Fact]
    public async Task An_agent_that_never_searches_cannot_answer()
    {
        _s.Llm.Handler = Agent((_, _) => Text("The Pro plan costs 20 dollars [1]."));

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.True(body.GetProperty("notFound").GetBoolean());
        Assert.Equal(0, body.GetProperty("toolCalls").GetInt32());
    }

    [Fact]
    public async Task Document_ids_in_the_request_limit_what_the_tools_can_search()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCall("search_documents", Search(QaScenario.PricingQuestion))
            : Text("NOT_FOUND"));

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion, _s.HolidaysTxt));

        Assert.True(body.GetProperty("notFound").GetBoolean());
        var userMessage = _s.Llm.Calls[0].Messages.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("holidays.txt", userMessage);
        Assert.DoesNotContain("plans.pdf", userMessage);
        Assert.Contains("No passage matched", ToolResults(_s.Llm.Calls[1]).Single()); // plans.pdf is out of scope
    }

    [Fact]
    public async Task Naming_a_document_outside_the_requests_scope_is_refused_by_the_tool()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCall("search_documents", Args(("query", QaScenario.PricingQuestion), ("documentIds", new[] { "plans.pdf" })))
            : Text("NOT_FOUND"));

        await Ask(_s.UserA, QaScenario.PricingQuestion, _s.HolidaysTxt);

        Assert.StartsWith("Error: no document 'plans.pdf'", ToolResults(_s.Llm.Calls[1]).Single());
    }

    [Fact]
    public async Task Invalid_tool_arguments_come_back_to_the_model_as_an_error_and_count_as_a_call()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCall("get_chunk_context", Args(("sourceIndex", 7), ("before", 9), ("after", 1)))
            : Text("NOT_FOUND"));

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.Equal(1, body.GetProperty("toolCalls").GetInt32());
        Assert.Contains("between 0 and 3", ToolResults(_s.Llm.Calls[1]).Single());
    }

    [Fact]
    public async Task Chunk_context_returns_the_neighbouring_passages_of_the_same_document_with_new_stable_indexes()
    {
        _s.Llm.Handler = Agent((turn, _) => turn switch
        {
            0 => ToolCall("search_documents", Search(QaScenario.PricingQuestion), "a"),
            1 => ToolCall("get_chunk_context", Args(("sourceIndex", 1), ("before", 1), ("after", 1)), "b"),
            _ => Text("The Pro plan costs 20 dollars per month [1]."),
        });

        var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

        Assert.Equal(2, body.GetProperty("toolCalls").GetInt32());
        var context = ToolResults(_s.Llm.Calls[2])[1];
        // The search already surfaced the neighbouring page, so it is referred to by its existing number instead of being sent twice.
        Assert.Contains("<source index=\"2\" note=\"already shown above\" />", context);
        Assert.DoesNotContain("99 dollars", context);
    }

    [Fact]
    public async Task Outline_of_a_document_lists_its_pages()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCall("get_document_outline", Args(("documentId", "plans.pdf")))
            : Text("NOT_FOUND"));

        await Ask(_s.UserA, QaScenario.PricingQuestion);

        var outline = ToolResults(_s.Llm.Calls[1]).Single();
        Assert.Contains("File: plans.pdf", outline);
        Assert.Contains("Pages: 2", outline);
        Assert.Contains("page 1: Pricing.", outline);
        Assert.Contains("page 2: Support.", outline);
    }

    [Fact]
    public async Task A_broken_agent_loop_falls_back_to_the_single_pass_answer()
    {
        _s.Llm.Handler = (call, _) => call.Options?.Tools is { Count: > 0 }
            ? throw new InvalidOperationException("agent exploded")
            : Task.FromResult(FakeChatClient.Reply("The Pro plan costs 20 dollars per month [1].", false));

        var response = await Ask(_s.UserA, QaScenario.PricingQuestion);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("The Pro plan costs 20 dollars per month [1].", body.GetProperty("answer").GetString());
        Assert.Equal(0, body.GetProperty("toolCalls").GetInt32());
        Assert.Equal(1, body.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task Single_pass_mode_can_be_selected_at_runtime()
    {
        var options = _s.Factory.Services.GetRequiredService<IOptions<QaOptions>>().Value;
        options.Mode = QaMode.SinglePass;
        try
        {
            _s.Llm.Handler = (_, _) => Task.FromResult(FakeChatClient.Reply("Pro is 20 dollars [1].", false));

            var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

            Assert.Equal("Pro is 20 dollars [1].", body.GetProperty("answer").GetString());
            var call = Assert.Single(_s.Llm.Calls);
            Assert.Null(call.Options!.Tools);
            Assert.Equal(0, body.GetProperty("toolCalls").GetInt32());
        }
        finally
        {
            options.Mode = QaMode.Agent;
        }
    }

    [Fact]
    public async Task Unreachable_model_gives_503_and_a_slow_agent_gives_504()
    {
        _s.Llm.Handler = (_, _) => throw new HttpRequestException("Connection refused");
        var unavailable = await Ask(_s.UserA, QaScenario.PricingQuestion);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.DoesNotContain("Connection refused", (await Json(unavailable)).ToString());

        _s.LlmFactory.Limits = new AgentLimits(10, TimeSpan.FromSeconds(1));
        _s.Llm.Handler = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Text("too late");
        };
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await Ask(_s.UserA, QaScenario.PricingQuestion)).StatusCode);
    }

    [Fact]
    public async Task Request_validation_matches_the_single_pass_endpoint()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(_s.UserA, "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(_s.UserA, QaScenario.PricingQuestion, _s.UserBPrices)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Ask(_s.EmptyUser, QaScenario.PricingQuestion)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Ask(_s.UnconfirmedUser, QaScenario.PricingQuestion)).StatusCode);
        Assert.Empty(_s.Llm.Calls);
    }

    [Fact]
    public async Task Extraction_notices_of_cited_documents_are_returned()
    {
        await using var db = DocumentTestHelpers.OpenDb(_s.ConnectionString);
        await db.Documents.IgnoreQueryFilters().Where(d => d.Id == _s.PlansPdf).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExtractionNotice, "Arabic text may be inaccurate."));
        try
        {
            _s.Llm.Handler = Agent((turn, _) => turn == 0
                ? ToolCall("search_documents", Search(QaScenario.PricingQuestion))
                : Text("The Pro plan costs 20 dollars per month [1]."));

            var body = await Json(await Ask(_s.UserA, QaScenario.PricingQuestion));

            Assert.Equal("plans.pdf: Arabic text may be inaccurate.", Assert.Single(body.GetProperty("notices").EnumerateArray().ToList()).GetString());
        }
        finally
        {
            await db.Documents.IgnoreQueryFilters().Where(d => d.Id == _s.PlansPdf).ExecuteUpdateAsync(s => s.SetProperty(d => d.ExtractionNotice, (string?)null));
        }
    }

    // ---- SSE (QA-10) ----

    [Fact]
    public async Task Stream_sends_progress_events_without_document_text_and_then_the_answer()
    {
        _s.Llm.Handler = Agent((turn, _) => turn == 0
            ? ToolCall("search_documents", Search(QaScenario.PricingQuestion))
            : Text("The Pro plan costs 20 dollars per month [1]."));

        var response = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var events = ParseSse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["searching", "tool_call", "verifying", "answer"], events.Select(e => e.Name));
        Assert.Equal("""{"tool":"search_documents"}""", events[1].Data);
        Assert.DoesNotContain("dollars", events[0].Data + events[1].Data + events[2].Data);
        var answer = JsonDocument.Parse(events[3].Data).RootElement;
        Assert.Equal("The Pro plan costs 20 dollars per month [1].", answer.GetProperty("answer").GetString());
        Assert.Equal(1, answer.GetProperty("toolCalls").GetInt32());
        Assert.Equal("plans.pdf", answer.GetProperty("citations")[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task Stream_reports_failures_as_an_error_event_and_validation_errors_as_normal_responses()
    {
        _s.Llm.Handler = (_, _) => throw new HttpRequestException("down");
        var failed = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = QaScenario.PricingQuestion });
        var events = ParseSse(await failed.Content.ReadAsStringAsync());
        Assert.Equal("error", events[^1].Name);
        Assert.Equal(503, JsonDocument.Parse(events[^1].Data).RootElement.GetProperty("status").GetInt32());

        var invalid = await _s.UserA.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _s.EmptyUser.Client.PostAsJsonAsync("/api/v1/ask/stream", new { question = "x" })).StatusCode);
        using var anonymous = _s.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/ask/stream", new { question = "x" })).StatusCode);
    }

    private static List<(string Name, string Data)> ParseSse(string body) =>
        [.. body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(block =>
        {
            var lines = block.Split('\n');
            return (lines.First(l => l.StartsWith("event: ")).Substring(7), lines.First(l => l.StartsWith("data: ")).Substring(6));
        })];
}
