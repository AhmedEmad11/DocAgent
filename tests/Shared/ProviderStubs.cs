using System.Net;
using System.Text;
using DocAgent.Core.Entities;

namespace DocAgent.TestSupport;

/// <summary>A scripted HTTP transport that records every request (method, URL, headers, body).</summary>
public sealed class StubTransport(Func<StubTransport.RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record RecordedRequest(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string Body);

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return respond(recorded);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public static class ProviderJson
{
    /// <summary>A plain text completion in the provider's format (OpenAI-compatible for OpenRouter, OpenAI and Gemini).</summary>
    public static string Text(LlmProvider provider, string text) => (provider == LlmProvider.Anthropic
        ? """{"id":"msg_1","type":"message","role":"assistant","model":"claude-test","content":[{"type":"text","text":@TEXT}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":2}}"""
        : """{"id":"c1","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":@TEXT},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}""")
        .Replace("@TEXT", System.Text.Json.JsonSerializer.Serialize(text));

    /// <summary>The model asks for one tool call.</summary>
    public static string ToolCall(LlmProvider provider, string name, string argumentsJson) => (provider == LlmProvider.Anthropic
        ? """{"id":"msg_2","type":"message","role":"assistant","model":"claude-test","content":[{"type":"tool_use","id":"toolu_1","name":@NAME,"input":@ARGS}],"stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":5,"output_tokens":2}}"""
        : """{"id":"c2","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":@NAME,"arguments":@ARGSTRING}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}""")
        .Replace("@NAME", System.Text.Json.JsonSerializer.Serialize(name))
        .Replace("@ARGSTRING", System.Text.Json.JsonSerializer.Serialize(argumentsJson))
        .Replace("@ARGS", argumentsJson);
}
