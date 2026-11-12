using DocAgent.Infrastructure.Qa;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Agents;

/// <summary>
/// Entry point of /ask. <c>Qa:Mode=Agent</c> (default) runs the agent; if the agent loop breaks for any reason other
/// than an unreachable model or a timeout, the question is answered by the single-pass pipeline instead (so the endpoint
/// degrades instead of failing). <c>Qa:Mode=SinglePass</c> uses the Phase 3 path only.
/// </summary>
public sealed class AskService(
    AgentQaService agent, QaService singlePass, IOptions<QaOptions> options, ILogger<AskService> logger)
{
    public async Task<QaAnswer> AskAsync(
        string question, IReadOnlyCollection<Guid>? documentIds, CancellationToken ct, Action<AgentEvent>? progress = null)
    {
        if (options.Value.Mode == QaMode.SinglePass)
        {
            progress?.Invoke(new AgentEvent("searching"));
            return await singlePass.AskAsync(question, documentIds, ct);
        }

        try
        {
            return await agent.AskAsync(question, documentIds, progress, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or LlmUnavailableException or QaTimeoutException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning("The agent run failed ({ExceptionType}); falling back to single-pass answering", ex.GetType().Name);
            return await singlePass.AskAsync(question, documentIds, ct);
        }
    }
}
