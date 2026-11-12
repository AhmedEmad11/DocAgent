using DocAgent.Infrastructure.Embeddings;
using DocAgent.Infrastructure.Ingestion;
using DocAgent.TestSupport;

namespace DocAgent.UnitTests.Ingestion;

public sealed class TextChunkerTests
{
    private static readonly EmbeddingTokenizer Tokenizer = EmbeddingTokenizer.Load(ModelLocator.Directory);
    private static readonly ChunkingOptions Defaults = new();
    private static readonly TextChunker Chunker = new(Tokenizer, Defaults);

    private static IReadOnlyList<TextChunk> Chunk(string text, int? page = null, string? section = null) =>
        Chunker.Chunk([new TextSegment(text, page, section)]);

    private static void AssertWithinLimits(IReadOnlyList<TextChunk> chunks)
    {
        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            var real = Tokenizer.CountTokens(chunk.Text);
            Assert.Equal(real, chunk.TokenCount);
            Assert.InRange(real, 1, Defaults.MaxTokens);
            // The text really sent to the model (prefix + special tokens) must fit its 512 window.
            Assert.True(Tokenizer.Encode(EmbeddingOptions.PassagePrefix + chunk.Text).Length <= 512);
        }
    }

    [Fact]
    public void Short_text_is_a_single_chunk()
    {
        var chunks = Chunk("A short paragraph.");
        Assert.Equal("A short paragraph.", Assert.Single(chunks).Text);
    }

    [Fact]
    public void Long_prose_never_exceeds_the_hard_maximum_and_is_near_the_target()
    {
        var chunks = Chunk(TestFiles.Prose(6000));

        AssertWithinLimits(chunks);
        Assert.True(chunks.Count > 5);
        // all but the last chunk are filled close to target (+ overlap)
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.InRange(c.TokenCount, 300, Defaults.MaxTokens));
    }

    [Fact]
    public void Ordinals_are_sequential_across_segments_and_metadata_is_kept()
    {
        var chunks = Chunker.Chunk(
        [
            new TextSegment(TestFiles.Prose(900), PageNumber: 1),
            new TextSegment("Tiny page.", PageNumber: 2),
            new TextSegment(TestFiles.Prose(900), Section: "Pricing"),
        ]);

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
        Assert.Contains(chunks, c => c.PageNumber == 2 && c.Text == "Tiny page.");
        Assert.All(chunks.Where(c => c.PageNumber is not null), c => Assert.Null(c.Section));
        Assert.All(chunks.Where(c => c.Section == "Pricing"), c => Assert.Null(c.PageNumber));
        Assert.Contains(chunks, c => c.PageNumber == 1);
        Assert.Contains(chunks, c => c.Section == "Pricing");
        AssertWithinLimits(chunks);
    }

    [Fact]
    public void Consecutive_chunks_overlap_by_up_to_the_configured_tokens()
    {
        var chunks = Chunk(TestFiles.Prose(3000));

        for (var i = 1; i < chunks.Count; i++)
        {
            var previousWords = chunks[i - 1].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var nextWords = chunks[i].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // The start of this chunk repeats the tail of the previous one.
            var overlapWords = Enumerable.Range(1, Math.Min(previousWords.Length, nextWords.Length))
                .Where(n => previousWords[^n..].SequenceEqual(nextWords[..n]))
                .DefaultIfEmpty(0).Max();
            Assert.True(overlapWords > 0, $"chunk {i} does not start with the end of chunk {i - 1}");
            var overlapTokens = Tokenizer.CountTokens(string.Join(' ', nextWords[..overlapWords]));
            Assert.InRange(overlapTokens, 1, Defaults.OverlapTokens);
        }
    }

    [Fact]
    public void All_text_is_covered_by_the_chunks()
    {
        var text = TestFiles.Prose(2500);
        var joined = string.Join(' ', Chunk(text).Select(c => c.Text));
        foreach (var sentence in text.Split(new[] { '.', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            Assert.Contains(sentence, joined);
        }
    }

    [Fact]
    public void One_giant_paragraph_without_punctuation_is_split_on_words()
    {
        var text = string.Join(' ', Enumerable.Range(0, 5000).Select(i => "word" + (i % 37)));
        AssertWithinLimits(Chunk(text));
    }

    [Fact]
    public void Text_without_any_whitespace_is_split_on_tokens()
    {
        var text = string.Concat(Enumerable.Range(0, 6000).Select(i => (char)('a' + (i * 7 % 26))));
        var chunks = Chunk(text);
        AssertWithinLimits(chunks);
        Assert.True(chunks.Count > 3);
    }

    [Fact]
    public void Arabic_text_is_chunked_within_limits_and_keeps_sentences()
    {
        var sentences = new[]
        {
            "تكلفة الخطة الشهرية عشرون دولارا وتشمل تخزين مائة مستند.",
            "عاصمة فرنسا هي باريس وهي أكبر مدنها.",
            "يمكنك إلغاء الاشتراك في أي وقت دون رسوم إضافية؟",
        };
        var text = string.Join(' ', Enumerable.Range(0, 400).Select(i => sentences[i % 3]));

        var chunks = Chunk(text, section: "الأسعار");

        AssertWithinLimits(chunks);
        Assert.True(chunks.Count > 2);
        Assert.All(chunks, c => Assert.Equal("الأسعار", c.Section));
        Assert.StartsWith("تكلفة الخطة", chunks[0].Text);
        Assert.Contains("عاصمة فرنسا هي باريس", string.Join(' ', chunks.Select(c => c.Text)));
    }

    [Fact]
    public void Paragraph_boundaries_are_preferred_split_points()
    {
        var para = string.Join(' ', Enumerable.Repeat("Sentence number one is here.", 40)); // about 280 tokens
        var text = string.Join("\n\n", Enumerable.Repeat(para, 6));

        var chunks = Chunk(text);

        AssertWithinLimits(chunks);
        // no chunk starts or ends in the middle of a sentence's first words: each unit is whole paragraph or overlap + whole paragraph
        Assert.All(chunks, c => Assert.EndsWith("here.", c.Text));
    }

    [Fact]
    public void Custom_limits_are_respected()
    {
        var small = new TextChunker(Tokenizer, new ChunkingOptions { TargetTokens = 50, MaxTokens = 60, OverlapTokens = 10 });
        var chunks = small.Chunk([new TextSegment(TestFiles.Prose(1000))]);
        Assert.All(chunks, c => Assert.InRange(c.TokenCount, 1, 60));
    }

    [Fact]
    public void Inconsistent_options_are_rejected() =>
        Assert.Throws<ArgumentException>(() => new TextChunker(Tokenizer, new ChunkingOptions { TargetTokens = 400, MaxTokens = 420, OverlapTokens = 60 }));

    [Fact]
    public void Empty_input_gives_no_chunks() => Assert.Empty(Chunker.Chunk([new TextSegment("   ")]));
}
