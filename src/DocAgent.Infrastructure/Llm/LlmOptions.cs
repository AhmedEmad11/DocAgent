namespace DocAgent.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Which provider serves requests. Phase 3 knows only "Ollama" (development).</summary>
    public string Provider { get; set; } = "Ollama";

    public OllamaOptions Ollama { get; set; } = new();

    public DemoOptions Demo { get; set; } = new();
}

/// <summary>The demo route: free OpenRouter models on the operator's key (SRS DEMO-1..6). Model IDs are configuration (DEMO-6).</summary>
public sealed class DemoOptions
{
    public const string DefaultModel = "google/gemma-4-31b-it:free";
    public const string DefaultFallbackModel = "google/gemma-4-26b-a4b-it:free";

    /// <summary>First choice. Chosen from OpenRouter's free tool-capable models (see PROGRESS.md).</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>Tried within the same request when the first model is rate limited or unavailable (DEMO-4). Empty = no fallback.</summary>
    public string? FallbackModel { get; set; } = DefaultFallbackModel;

    /// <summary>The operator's OpenRouter key. A secret: user-secrets in development, a Container Apps secret in production. Never logged or returned.</summary>
    public string? OpenRouterApiKey { get; set; }

    /// <summary>Questions per user per UTC day (DEMO-2); summaries and comparisons count as one each.</summary>
    public int DailyQuestionLimit { get; set; } = 10;

    /// <summary>
    /// Demo requests per minute across all users (the shared operator key's own throttle; see <see cref="DemoGlobalThrottle"/>).
    /// One request is one ask, stream, summary or comparison; each of those makes several model calls, so keep this at or
    /// below the free models' per-minute limit divided by the calls per request you expect. 0 = no local limit.
    /// </summary>
    public int GlobalRequestsPerMinute { get; set; } = 20;
}

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "gemma4:12b";

    /// <summary>Whether the model "thinks" before answering. Off by default: about 3x faster with the same answers (measured, see PROGRESS.md). Null leaves the model default (on for gemma4).</summary>
    public bool? Think { get; set; } = false;

    /// <summary>Context window requested from Ollama. Its server default (4096) would silently truncate long prompts.</summary>
    public int NumCtx { get; set; } = 8192;

    /// <summary>Retries when the server cannot be reached or answers 502/503/504 (REL-2); 0 = none.</summary>
    public int Retries { get; set; } = 2;

    /// <summary>Delay before the first retry; it doubles each time.</summary>
    public int RetryDelayMilliseconds { get; set; } = 250;
}
