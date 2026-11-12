using DocAgent.Infrastructure.Embeddings;

namespace DocAgent.TestSupport;

/// <summary>Finds the downloaded embedding model; fails with a clear message when it is missing.</summary>
public static class ModelLocator
{
    public static string Directory { get; } = Find();

    private static string Find()
    {
        var dir = ModelPathResolver.Resolve("models/multilingual-e5-small");
        if (!File.Exists(Path.Combine(dir, EmbeddingOptions.OnnxFileName))
            || !File.Exists(Path.Combine(dir, EmbeddingOptions.SentencePieceFileName)))
        {
            throw new InvalidOperationException(
                "The embedding model is missing. Run scripts/download-models.sh from the repository root, then re-run the tests. "
                + $"(looked for {EmbeddingOptions.OnnxFileName} in '{dir}')");
        }

        return dir;
    }
}
