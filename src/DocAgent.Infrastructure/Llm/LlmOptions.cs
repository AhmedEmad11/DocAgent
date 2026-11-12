namespace DocAgent.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Which provider serves requests. Phase 3 knows only "Ollama" (development).</summary>
    public string Provider { get; set; } = "Ollama";

    public OllamaOptions Ollama { get; set; } = new();
}

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "gemma4:12b";

    /// <summary>Whether the model "thinks" before answering. Off by default: about 3x faster with the same answers (measured, see PROGRESS.md). Null leaves the model default (on for gemma4).</summary>
    public bool? Think { get; set; } = false;

    /// <summary>Context window requested from Ollama. Its server default (4096) would silently truncate long prompts.</summary>
    public int NumCtx { get; set; } = 8192;
}
