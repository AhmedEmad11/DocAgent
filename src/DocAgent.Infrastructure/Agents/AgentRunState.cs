using System.Diagnostics;

namespace DocAgent.Infrastructure.Agents;

/// <summary>Why the agent stopped calling tools before it chose to (SRS QA-11).</summary>
public enum StopReason
{
    None,
    ToolCallCap,
    ContextBudget,
    SoftDeadline,
}

/// <summary>Mutable counters of one agent run: tool calls, context budget and the soft deadline.</summary>
public sealed class AgentRunState(int maxToolCalls, int contextBudgetTokens, TimeSpan softDeadline)
{
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _toolCalls;
    private int _contextTokens;
    private bool _incomplete;

    public int MaxToolCalls { get; } = maxToolCalls;

    public int ToolCalls
    {
        get
        {
            lock (_gate)
            {
                return _toolCalls;
            }
        }
    }

    public int ContextTokensUsed
    {
        get
        {
            lock (_gate)
            {
                return _contextTokens;
            }
        }
    }

    public int ContextTokensLeft => Math.Max(0, contextBudgetTokens - ContextTokensUsed);

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    /// <summary>Counts a call; false when the cap was already used up (the call must not run).</summary>
    public bool TryBeginToolCall()
    {
        lock (_gate)
        {
            if (_toolCalls >= MaxToolCalls)
            {
                _incomplete = true; // the model wanted more than it is allowed
                return false;
            }

            _toolCalls++;
            return true;
        }
    }

    public void AddContextTokens(int tokens)
    {
        lock (_gate)
        {
            _contextTokens += tokens;
        }
    }

    /// <summary>A nested workflow (summary, comparison) ran out of time and returned partial results.</summary>
    public void MarkIncomplete()
    {
        lock (_gate)
        {
            _incomplete = true;
        }
    }

    /// <summary>The first limit that was reached, or None. Once not None, tools are withdrawn from the model.</summary>
    public StopReason Reason
    {
        get
        {
            lock (_gate)
            {
                if (_toolCalls >= MaxToolCalls)
                {
                    return StopReason.ToolCallCap;
                }

                if (_contextTokens >= contextBudgetTokens)
                {
                    return StopReason.ContextBudget;
                }
            }

            return Elapsed >= softDeadline ? StopReason.SoftDeadline : StopReason.None;
        }
    }

    /// <summary>True when a limit cut the research short, so the answer may be incomplete (QA-11).</summary>
    public bool Incomplete
    {
        get
        {
            lock (_gate)
            {
                return _incomplete || _toolCalls >= MaxToolCalls || _contextTokens >= contextBudgetTokens;
            }
        }
    }
}
