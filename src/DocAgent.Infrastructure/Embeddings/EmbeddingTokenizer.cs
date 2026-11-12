using Microsoft.ML.Tokenizers;

namespace DocAgent.Infrastructure.Embeddings;

/// <summary>
/// The XLM-R SentencePiece tokenizer of multilingual-e5-small. Thread-safe.
/// The model's ids differ from the raw SentencePiece ids (fairseq layout): &lt;s&gt;=0, &lt;pad&gt;=1,
/// &lt;/s&gt;=2, &lt;unk&gt;=3 and every other piece id is the SentencePiece id + 1.
/// </summary>
public sealed class EmbeddingTokenizer : ITokenCounter
{
    public const int BosId = 0;
    public const int PadId = 1;
    public const int EosId = 2;
    public const int UnkId = 3;
    private const int Offset = 1;

    private readonly SentencePieceTokenizer _tokenizer;

    private EmbeddingTokenizer(SentencePieceTokenizer tokenizer) => _tokenizer = tokenizer;

    public static EmbeddingTokenizer Load(string modelDirectory)
    {
        var file = Path.Combine(modelDirectory, EmbeddingOptions.SentencePieceFileName);
        if (!File.Exists(file))
        {
            throw new FileNotFoundException(ModelPathResolver.MissingModelMessage(modelDirectory));
        }

        using var stream = File.OpenRead(file);
        return new EmbeddingTokenizer(SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false));
    }

    /// <summary>Model token ids for the text; with <paramref name="addSpecialTokens"/> wrapped in &lt;s&gt; ... &lt;/s&gt;.</summary>
    public int[] Encode(string text, bool addSpecialTokens = true)
    {
        var raw = _tokenizer.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        var ids = new List<int>(raw.Count + 2);
        if (addSpecialTokens)
        {
            ids.Add(BosId);
        }

        foreach (var id in raw)
        {
            ids.Add(id == 0 ? UnkId : id + Offset);
        }

        if (addSpecialTokens)
        {
            ids.Add(EosId);
        }

        return [.. ids];
    }

    /// <summary>Number of tokens in the text, excluding special tokens.</summary>
    public int CountTokens(string text) =>
        text.Length == 0 ? 0 : _tokenizer.CountTokens(text, addBeginningOfSentence: false, addEndOfSentence: false);

    /// <summary>
    /// Length (in characters of <paramref name="text"/>) of the longest prefix that has at most
    /// <paramref name="maxTokens"/> tokens. Returns 0 if not even one token fits.
    /// </summary>
    public int GetPrefixLength(string text, int maxTokens)
    {
        var index = _tokenizer.GetIndexByTokenCount(text, addBeginningOfSentence: false, addEndOfSentence: false,
            maxTokenCount: maxTokens, out var normalized, out _);
        // The index refers to the normalized text. When normalization did not change the text it is exact;
        // otherwise approximate by proportion and let the caller re-verify.
        if (normalized is null || string.Equals(normalized, text, StringComparison.Ordinal))
        {
            return Math.Clamp(index, 0, text.Length);
        }

        return Math.Clamp((int)((long)index * text.Length / Math.Max(1, normalized.Length)), 0, text.Length);
    }
}
