using Xunit.Abstractions;
using DocAgent.Infrastructure.Embeddings;
using DocAgent.TestSupport;
using Microsoft.Extensions.AI;

namespace DocAgent.UnitTests.Ingestion;

/// <summary>Loads the real int8 model; needs scripts/download-models.sh to have been run.</summary>
public sealed class OnnxEmbeddingGeneratorTests(ITestOutputHelper output) : IDisposable
{
    private readonly OnnxEmbeddingGenerator _generator =
        new(ModelLocator.Directory, EmbeddingTokenizer.Load(ModelLocator.Directory));

    public void Dispose() => _generator.Dispose();

    private async Task<float[]> Embed(string text) => (await _generator.GenerateAsync([text]))[0].Vector.ToArray();

    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum(); // vectors are unit length

    [Fact]
    public async Task Embeddings_have_384_dimensions_and_unit_length()
    {
        var embeddings = await _generator.GenerateAsync(["query: hello", "passage: a much longer text about something else entirely"]);
        Assert.Equal(2, embeddings.Count);
        foreach (var e in embeddings)
        {
            Assert.Equal(384, e.Vector.Length);
            Assert.Equal(1.0, Math.Sqrt(e.Vector.ToArray().Sum(x => (double)x * x)), 3);
        }
    }

    // Dynamic int8 quantization picks activation scales per tensor, so a text's vector varies very slightly
    // with what else is in its batch (observed cosine ~0.995). Retrieval is unaffected; just not bit-identical.
    [Fact]
    public async Task Batched_result_matches_single_result()
    {
        var texts = new[] { "passage: short", "passage: " + string.Join(' ', Enumerable.Repeat("a somewhat longer sentence", 20)), "passage: medium length text here" };
        var batched = await _generator.GenerateAsync(texts);
        for (var i = 0; i < texts.Length; i++)
        {
            var c = Cosine(batched[i].Vector.ToArray(), await Embed(texts[i])); Assert.True(c > 0.98, $"cosine {c:F4} for text {i}");
        }
    }

    [Fact]
    public async Task Related_english_pair_scores_clearly_higher_than_an_unrelated_passage()
    {
        var query = await Embed("query: how much does the plan cost?");
        var related = await Embed("passage: The plan costs 20 dollars per month.");
        var unrelated = await Embed("passage: Giraffes are the tallest land animals and eat leaves from acacia trees.");

        var relatedScore = Cosine(query, related);
        var unrelatedScore = Cosine(query, unrelated);
        output.WriteLine($"EN pair: related {relatedScore:F3}, unrelated {unrelatedScore:F3}");
        Assert.True(relatedScore > unrelatedScore + 0.05, $"related {relatedScore:F3} vs unrelated {unrelatedScore:F3}");
    }

    [Fact]
    public async Task English_query_finds_the_matching_Arabic_passage()
    {
        var query = await Embed("query: what is the capital of France?");
        var related = await Embed("passage: باريس هي عاصمة فرنسا");
        var unrelated = await Embed("passage: الزرافة هي أطول الحيوانات التي تعيش على اليابسة");

        var relatedScore = Cosine(query, related);
        var unrelatedScore = Cosine(query, unrelated);
        output.WriteLine($"EN->AR pair: related {relatedScore:F3}, unrelated {unrelatedScore:F3}");
        Assert.True(relatedScore > unrelatedScore + 0.03, $"related {relatedScore:F3} vs unrelated {unrelatedScore:F3}");
    }

    [Fact]
    public async Task Inputs_longer_than_the_model_limit_are_truncated_not_rejected()
    {
        var huge = "passage: " + string.Join(' ', Enumerable.Repeat("word", 2000));
        Assert.Equal(384, (await Embed(huge)).Length);
    }
}
