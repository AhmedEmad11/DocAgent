namespace DocAgent.Infrastructure.Qa;

public sealed class QaOptions
{
    public const string SectionName = "Qa";

    /// <summary>How /ask answers: "Agent" (tools, verification; default) or "SinglePass" (Phase 3 retrieve-and-prompt).</summary>
    public QaMode Mode { get; set; } = QaMode.Agent;

    /// <summary>Overall time allowed for one question (retrieval plus the model call).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Upper bound for the model's reply, including any thinking tokens.</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>Low temperature: answers should follow the sources, not be creative.</summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>Maximum length of the quote returned with each citation.</summary>
    public int QuoteMaxChars { get; set; } = 300;
}

public enum QaMode
{
    Agent,
    SinglePass,
}
