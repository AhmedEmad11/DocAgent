using DocAgent.Infrastructure.Embeddings;
using DocAgent.TestSupport;

namespace DocAgent.UnitTests.Ingestion;

public sealed class EmbeddingTokenizerTests
{
    private static readonly EmbeddingTokenizer Tokenizer = EmbeddingTokenizer.Load(ModelLocator.Directory);

    // Expected ids were produced by the Hugging Face `tokenizers` library from the model repo's
    // tokenizer.json (Xenova/multilingual-e5-small @ 761b726d), i.e. the reference implementation.
    public static TheoryData<string, int[]> ReferenceCases => new()
    {
        { "query: hello world", [0, 41, 1294, 12, 33600, 31, 8999, 2] },
        { "passage: The plan costs 20 dollars per month.", [0, 46692, 12, 581, 1774, 84458, 387, 86258, 117, 31150, 5, 2] },
        { "query: what is the capital of France?", [0, 41, 1294, 12, 2367, 83, 70, 10323, 111, 9942, 32, 2] },
        { "passage: باريس هي عاصمة فرنسا", [0, 46692, 12, 167448, 3992, 6, 167792, 142347, 2] },
        { "passage: Hello,   World!\tfoo", [0, 46692, 12, 35378, 4, 6661, 38, 5775, 31, 2] },
    };

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void Token_ids_match_the_reference_tokenizer(string text, int[] expected) =>
        Assert.Equal(expected, Tokenizer.Encode(text));

    [Fact]
    public void CountTokens_excludes_special_tokens()
    {
        Assert.Equal(6, Tokenizer.CountTokens("query: hello world"));
        Assert.Equal(0, Tokenizer.CountTokens(""));
    }

    [Fact]
    public void GetPrefixLength_returns_a_prefix_within_the_budget()
    {
        var text = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 50));
        var length = Tokenizer.GetPrefixLength(text, 100);
        Assert.InRange(length, 1, text.Length - 1);
        Assert.True(Tokenizer.CountTokens(text[..length]) <= 100);
    }
}
