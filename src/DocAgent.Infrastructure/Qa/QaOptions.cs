namespace DocAgent.Infrastructure.Qa;

public sealed class QaOptions
{
    public const string SectionName = "Qa";

    /// <summary>Overall time allowed for one question (retrieval plus the model call).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Upper bound for the model's reply, including any thinking tokens.</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>Low temperature: answers should follow the sources, not be creative.</summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>Maximum length of the quote returned with each citation.</summary>
    public int QuoteMaxChars { get; set; } = 300;
}
