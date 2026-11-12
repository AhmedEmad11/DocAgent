using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocAgent.Infrastructure.Agents;

/// <summary>One progress event of an agent run (SSE, QA-10). Carries a tool name at most, never document text or arguments.</summary>
public sealed record AgentEvent(string Type, string? Tool = null);

/// <summary>
/// Wraps a tool: counts the call against the cap, refuses it once the cap is used up, turns failures into a short error
/// text the model can react to, and records an activity per call with name, duration and size only (SEC-2, OBS-1).
/// </summary>
internal sealed class CountedFunction(
    AIFunction inner, AgentRunState state, ILogger logger, Action<AgentEvent>? progress) : DelegatingAIFunction(inner)
{
    public static readonly ActivitySource ActivitySource = new("DocAgent.Agent");

    public const string CapMessage =
        "Tool call refused: the tool-call limit is reached. Do not call any more tools; answer with the sources you already have, or reply NOT_FOUND.";

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("agent.tool");
        activity?.SetTag("tool.name", Name);
        if (!state.TryBeginToolCall())
        {
            activity?.SetTag("tool.refused", true);
            logger.LogInformation("Agent tool {Tool} refused: call cap reached", Name);
            return CapMessage;
        }

        progress?.Invoke(new AgentEvent("tool_call", Name));
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            activity?.SetTag("tool.result_chars", (result as string)?.Length ?? 0);
            return result;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or Qa.LlmUnavailableException or Qa.QaTimeoutException))
        {
            // Argument binding errors and tool bugs: tell the model briefly, log the type only (the message may hold document text).
            activity?.SetTag("tool.error", ex.GetType().Name);
            logger.LogWarning("Agent tool {Tool} failed with {ExceptionType}", Name, ex.GetType().Name);
            return ex is ArgumentException or System.Text.Json.JsonException or InvalidOperationException
                ? $"Error: invalid arguments for {Name}. Check the parameter names and types in the tool description and try again."
                : $"Error: {Name} failed. Try something else or answer with what you have.";
        }
        finally
        {
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            activity?.SetTag("tool.duration_ms", Math.Round(ms));
            logger.LogInformation("Agent tool {Tool} finished in {ElapsedMs:F0} ms", Name, ms);
        }
    }
}
