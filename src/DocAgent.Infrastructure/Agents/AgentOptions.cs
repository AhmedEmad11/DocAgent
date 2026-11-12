using DocAgent.Infrastructure.Llm;

namespace DocAgent.Infrastructure.Agents;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public AgentLimitsSet Limits { get; set; } = new();

    /// <summary>Chunks returned by one search_documents call (the retriever's own TopK/MinScore still apply).</summary>
    public int SearchTopK { get; set; } = 5;

    /// <summary>
    /// Budget for the source text the tools may put into the conversation, in embedding-tokenizer tokens. The model's
    /// context is small (gemma4 NumCtx 8192) and Ollama silently drops the oldest messages on overflow, so tools stop
    /// returning text once this is used up (the answer is then marked incomplete).
    /// </summary>
    public int ContextBudgetTokens { get; set; } = 3600;

    /// <summary>
    /// Upper bound for one agent model turn (a tool call or the final answer). Answers are short; a model that starts
    /// repeating itself is cut off after this many tokens instead of running to <c>Qa:MaxOutputTokens</c> (seen once per
    /// ~10 eval runs of one question: 50-70 s turns).
    /// </summary>
    public int MaxOutputTokens { get; set; } = 1024;

    /// <summary>Share of the timeout after which the agent stops calling tools and answers with what it has.</summary>
    public double SoftDeadlineFraction { get; set; } = 0.7;

    /// <summary>Largest source text one map call of summarize/compare may hold (embedding-tokenizer tokens).</summary>
    public int MapBudgetTokens { get; set; } = 2800;

    /// <summary>Upper bound for the output of one map/reduce call.</summary>
    public int WorkflowMaxOutputTokens { get; set; } = 900;
}

public sealed class AgentLimitsSet
{
    /// <summary>Until demo and BYOK routes exist (Phases 5-6) the development route uses the BYOK values.</summary>
    public AgentLimitsOptions Development { get; set; } = new() { MaxToolCalls = 10, TimeoutSeconds = 120 };

    public AgentLimitsOptions Demo { get; set; } = new() { MaxToolCalls = 4, TimeoutSeconds = 60 };

    public AgentLimitsOptions Byok { get; set; } = new() { MaxToolCalls = 10, TimeoutSeconds = 120 };
}

public sealed class AgentLimitsOptions
{
    public int MaxToolCalls { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 120;

    public AgentLimits ToLimits() => new(Math.Max(1, MaxToolCalls), TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds)));
}
