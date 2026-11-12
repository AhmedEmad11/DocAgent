using DocAgent.Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocAgent.UnitTests.Agents;

public sealed class LimitEnforcementTests
{
    private static AgentRunState State(int max = 2, int budget = 1000, double softSeconds = 1000) =>
        new(max, budget, TimeSpan.FromSeconds(softSeconds));

    private static AIFunction Echo(Action? onRun = null) =>
        AIFunctionFactory.Create(() => { onRun?.Invoke(); return "ran"; }, "echo", "echo");

    [Fact]
    public async Task Calls_beyond_the_cap_are_refused_without_running_the_tool_and_mark_the_answer_incomplete()
    {
        var state = State(max: 2);
        var runs = 0;
        var tool = new CountedFunction(Echo(() => runs++), state, NullLogger.Instance, null);

        var results = new List<object?>();
        for (var i = 0; i < 4; i++)
        {
            results.Add((await tool.InvokeAsync())?.ToString());
        }

        Assert.Equal(2, runs);
        Assert.Equal(2, state.ToolCalls);
        Assert.Equal(new object?[] { "ran", "ran", CountedFunction.CapMessage, CountedFunction.CapMessage }, results);
        Assert.Equal(StopReason.ToolCallCap, state.Reason);
        Assert.True(state.Incomplete);
    }

    [Fact]
    public async Task Progress_events_carry_only_the_tool_name()
    {
        var events = new List<AgentEvent>();
        var tool = new CountedFunction(Echo(), State(), NullLogger.Instance, events.Add);

        await tool.InvokeAsync(new AIFunctionArguments());

        Assert.Equal([new AgentEvent("tool_call", "echo")], events);
    }

    [Fact]
    public async Task A_tool_that_throws_gives_the_model_a_short_error_not_the_exception_text()
    {
        var failing = AIFunctionFactory.Create(string () => throw new InvalidOperationException("secret document text"), "bad", "bad");
        var tool = new CountedFunction(failing, State(), NullLogger.Instance, null);

        var result = (string?)await tool.InvokeAsync();

        Assert.StartsWith("Error: invalid arguments for bad", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public async Task Once_the_cap_is_reached_the_model_is_called_without_tools_and_told_to_answer()
    {
        var state = State(max: 1);
        var inner = new StubChatClient((_, _, _) => StubChatClient.Text("final"));
        var client = new ToolLimitChatClient(inner, state);
        var options = new ChatOptions { Tools = [Echo()], ToolMode = ChatToolMode.Auto };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], options); // before the cap: tools kept
        Assert.NotNull(inner.Calls[0].Options!.Tools);

        Assert.True(state.TryBeginToolCall());
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], options);

        var limited = inner.Calls[1];
        Assert.Null(limited.Options!.Tools);
        Assert.Null(limited.Options.ToolMode);
        Assert.Equal(ToolLimitChatClient.LimitNotice, limited.Messages[^1].Text);
        Assert.NotNull(options.Tools); // the caller's options are not modified
    }

    [Fact]
    public async Task The_context_budget_withdraws_the_tools_too()
    {
        var state = State(max: 10, budget: 100);
        state.AddContextTokens(120);
        var inner = new StubChatClient((_, _, _) => StubChatClient.Text("final"));

        await new ToolLimitChatClient(inner, state).GetResponseAsync([new ChatMessage(ChatRole.User, "q")], new ChatOptions { Tools = [Echo()] });

        Assert.Equal(StopReason.ContextBudget, state.Reason);
        Assert.Null(inner.Calls[0].Options!.Tools);
        Assert.True(state.Incomplete);
    }

    [Fact]
    public async Task The_soft_deadline_withdraws_the_tools_and_marks_incomplete()
    {
        var state = State(softSeconds: 0);
        var inner = new StubChatClient((_, _, _) => StubChatClient.Text("final"));

        await new ToolLimitChatClient(inner, state).GetResponseAsync([new ChatMessage(ChatRole.User, "q")], new ChatOptions { Tools = [Echo()] });

        Assert.Null(inner.Calls[0].Options!.Tools);
        Assert.True(state.Incomplete);
    }

    [Fact]
    public void Limits_are_data_the_route_carries_and_the_defaults_match_the_srs()
    {
        var options = new AgentOptions();

        Assert.Equal(new DocAgent.Infrastructure.Llm.AgentLimits(10, TimeSpan.FromSeconds(120)), options.Limits.Development.ToLimits());
        Assert.Equal(new DocAgent.Infrastructure.Llm.AgentLimits(4, TimeSpan.FromSeconds(60)), options.Limits.Demo.ToLimits());
        Assert.Equal(new DocAgent.Infrastructure.Llm.AgentLimits(10, TimeSpan.FromSeconds(120)), options.Limits.Byok.ToLimits());
    }
}
