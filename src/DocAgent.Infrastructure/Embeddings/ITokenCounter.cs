namespace DocAgent.Infrastructure.Embeddings;

/// <summary>Token arithmetic the chunker needs; implemented by the embedding model's tokenizer.</summary>
public interface ITokenCounter
{
    /// <summary>Number of tokens in the text, excluding special tokens.</summary>
    int CountTokens(string text);

    /// <summary>Length in characters of the longest prefix with at most <paramref name="maxTokens"/> tokens (0 if none fits).</summary>
    int GetPrefixLength(string text, int maxTokens);
}
