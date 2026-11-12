namespace DocAgent.Infrastructure.Ingestion;

public sealed class ChunkingOptions
{
    public const string SectionName = "Chunking";

    /// <summary>Chunks are filled up to about this many tokens.</summary>
    public int TargetTokens { get; set; } = 400;

    /// <summary>Hard cap: no chunk ever has more tokens than this (the model reads 512 including the prefix).</summary>
    public int MaxTokens { get; set; } = 480;

    /// <summary>Tokens repeated from the end of the previous chunk at the start of the next one.</summary>
    public int OverlapTokens { get; set; } = 60;
}

public sealed class IngestionOptions
{
    public const string SectionName = "Documents:Ingestion";

    public int ParseTimeoutSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>Zip-bomb guard for .docx: total declared uncompressed size.</summary>
    public long MaxDocxUncompressedBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Zip-bomb guard for .docx: number of archive entries.</summary>
    public int MaxDocxEntries { get; set; } = 2000;

    /// <summary>PDFs with more pages are rejected (bounds parse time).</summary>
    public int MaxPdfPages { get; set; } = 2000;

    /// <summary>A PDF whose average non-whitespace characters per page is below this is treated as scanned.</summary>
    public int MinCharsPerPdfPage { get; set; } = 20;
}

public sealed class DocumentLimit
{
    public int MaxDocuments { get; set; }
    public long MaxFileBytes { get; set; }
}

public sealed class DocumentLimitsOptions
{
    public const string SectionName = "Documents:Limits";

    /// <summary>Users without an LLM key (SRS D19).</summary>
    public DocumentLimit Demo { get; set; } = new() { MaxDocuments = 3, MaxFileBytes = 5L * 1024 * 1024 };

    /// <summary>Users with their own LLM key (SRS D19).</summary>
    public DocumentLimit Byok { get; set; } = new() { MaxDocuments = 100, MaxFileBytes = 25L * 1024 * 1024 };
}
