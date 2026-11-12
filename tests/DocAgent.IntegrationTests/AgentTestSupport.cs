using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace DocAgent.IntegrationTests;

/// <summary>Helpers for scripting the fake chat client as an agent that calls tools, plus the verifier and the map-reduce workflows.</summary>
public static partial class AgentScript
{
    public static ChatResponse ToolCall(string name, Dictionary<string, object?> arguments, string id = "call-1") =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, arguments)]));

    public static ChatResponse ToolCalls(params (string Name, Dictionary<string, object?> Arguments)[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, calls.Select((c, i) => (AIContent)new FunctionCallContent($"call-{i}", c.Name, c.Arguments)).ToList()));

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    public static bool IsVerifier(FakeChatClient.Call call) =>
        call.Messages.Count > 0 && call.Messages[0].Text.Contains("strict fact checker");

    public static bool IsAgent(FakeChatClient.Call call) => call.Options?.Instructions is { Length: > 0 } && !IsVerifier(call);

    /// <summary>The tool results the model has seen so far, in order.</summary>
    public static List<string> ToolResults(FakeChatClient.Call call) =>
        [.. call.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? "")];

    /// <summary>A verifier reply that supports every claim, except those whose text contains one of <paramref name="unsupportedMarkers"/>.</summary>
    public static ChatResponse Verdicts(FakeChatClient.Call call, params string[] unsupportedMarkers)
    {
        var claims = new List<object>();
        foreach (Match m in ClaimLine().Matches(call.Messages[1].Text))
        {
            var supported = !unsupportedMarkers.Any(u => m.Groups[3].Value.Contains(u, StringComparison.OrdinalIgnoreCase));
            claims.Add(new { id = int.Parse(m.Groups[1].Value), supported, source = supported ? int.Parse(m.Groups[2].Value.Split(',')[0].Trim()) : 0 });
        }

        return Text(JsonSerializer.Serialize(new { claims }));
    }

    /// <summary>
    /// A handler for the agent flow: the verifier is answered by <see cref="Verdicts"/>, agent turns by <paramref name="agentTurn"/>
    /// (given the 0-based turn number and the call).
    /// </summary>
    public static Func<FakeChatClient.Call, CancellationToken, Task<ChatResponse>> Agent(
        Func<int, FakeChatClient.Call, ChatResponse> agentTurn, params string[] unsupportedMarkers)
    {
        var turn = 0;
        return (call, _) => Task.FromResult(IsVerifier(call) ? Verdicts(call, unsupportedMarkers) : agentTurn(Interlocked.Increment(ref turn) - 1, call));
    }

    /// <summary>Handler for summary/compare: echoes the source numbers it was given so that citations can be checked.</summary>
    public static Func<FakeChatClient.Call, CancellationToken, Task<ChatResponse>> Workflow(Func<FakeChatClient.Call, CancellationToken, Task>? before = null) => async (call, ct) =>
    {
        if (before is not null)
        {
            await before(call, ct);
        }

        var system = call.Messages[0].Text;
        var user = call.Messages[1].Text;
        if (system.Contains("You compare two documents"))
        {
            var docs = Regex.Matches(user, "<document n=\"([12])\"[^>]*>(.*?)</document>", RegexOptions.Singleline);
            var lines = docs.Select(d => $"- Topic: {(d.Groups[1].Value == "1" ? "A" : "B")} says " + string.Concat(Markers(d.Groups[2].Value).Select(n => $"[{n}]")));
            return Text(string.Join("\n", lines));
        }

        if (system.Contains("You merge partial summaries"))
        {
            return Text("- merged " + string.Concat(Markers(user).Distinct().Select(n => $"[{n}]")));
        }

        var sources = SourceIndex().Matches(user).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return Text(string.Join("\n", sources.Select(n => $"- point from source {n} [{n}]")));
    };

    private static IEnumerable<int> Markers(string text) => MarkerNumber().Matches(text).Select(m => int.Parse(m.Groups[1].Value));

    [GeneratedRegex(@"^(\d+) \[cites ([\d, ]+)\] (.*)$", RegexOptions.Multiline)]
    private static partial Regex ClaimLine();

    [GeneratedRegex(@"<source index=""(\d+)""")]
    private static partial Regex SourceIndex();

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex MarkerNumber();
}
