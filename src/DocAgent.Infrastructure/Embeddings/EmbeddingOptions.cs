namespace DocAgent.Infrastructure.Embeddings;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>Model name stored with every chunk (SRS C3: changing the model requires re-embedding).</summary>
    public const string ModelName = "multilingual-e5-small-int8";

    /// <summary>Prefix the model requires on document passages.</summary>
    public const string PassagePrefix = "passage: ";

    /// <summary>Prefix the model requires on search queries.</summary>
    public const string QueryPrefix = "query: ";

    public const string OnnxFileName = "model_quantized.onnx";
    public const string SentencePieceFileName = "sentencepiece.bpe.model";

    /// <summary>
    /// Directory with the model files (see scripts/download-models.sh). A relative path is resolved
    /// against the content root, the app base directory and their parents.
    /// </summary>
    public string ModelPath { get; set; } = "models/multilingual-e5-small";

    /// <summary>Hard limit of the model: longer inputs are truncated.</summary>
    public int MaxSequenceLength { get; set; } = 512;

    public int BatchSize { get; set; } = 16;
}
