using Microsoft.Extensions.AI;

namespace DocAgent.Infrastructure.Agents;

/// <summary>
/// Sits between the function-invoking layer and the real chat client. Once a limit is reached (tool-call cap, context
/// budget, soft deadline) it withdraws the tools from the request and tells the model to answer with what it has, so
/// the loop ends with an answer instead of an error (SRS QA-11).
/// </summary>
public sealed class ToolLimitChatClient(IChatClient inner, AgentRunState state) : DelegatingChatClient(inner)
{
    public const string LimitNotice =
        "Search limit reached: you cannot call any more tools. Write the final answer now using only the sources already retrieved. "
        + "If they do not contain the answer, reply NOT_FOUND. Keep the same answer format and language.";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        (messages, options) = Apply(messages, options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        (messages, options) = Apply(messages, options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private (IEnumerable<ChatMessage>, ChatOptions?) Apply(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var reason = state.Reason;
        if (reason == StopReason.None || options?.Tools is not { Count: > 0 })
        {
            return (messages, options);
        }

        if (reason == StopReason.SoftDeadline)
        {
            state.MarkIncomplete();
        }

        var limited = options.Clone();
        limited.Tools = null;
        limited.ToolMode = null;
        return ([.. messages, new ChatMessage(ChatRole.User, LimitNotice)], limited);
    }
}
