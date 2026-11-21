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

    /// <summary>ONNX Runtime intra-op threads. 0 = the CPU count the process may use (honours container CPU limits).</summary>
    public int IntraOpThreads { get; set; }

    /// <summary>
    /// ONNX Runtime's CPU memory arena keeps every block it ever grew to (it never returns memory to the OS); with large attention
    /// intermediates that pins hundreds of MB on a small container. Off = plain allocations that are freed after each batch (slightly slower, much lower resident set).
    /// </summary>
    public bool UseCpuMemoryArena { get; set; } = true;

    /// <summary>ONNX Runtime memory-pattern planning: it pre-allocates one block sized for the largest input seen. Off for small containers.</summary>
    public bool UseMemoryPattern { get; set; } = true;

    /// <summary>Return freed native memory to the OS after the model loads and during embedding (glibc <c>malloc_trim</c>; Linux only). Keeps the resident set near the working set.</summary>
    public bool TrimNativeHeap { get; set; } = true;

    /// <summary>Lower the peak while the model loads: weights bypass the arena and are not pre-packed into a second copy (a little slower per batch).</summary>
    public bool LowMemoryLoad { get; set; }
}
