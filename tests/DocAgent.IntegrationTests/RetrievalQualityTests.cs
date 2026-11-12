using System.Text;
using DocAgent.Infrastructure.Retrieval;
using DocAgent.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace DocAgent.IntegrationTests;

/// <summary>
/// Measures retrieval quality on a small English/Arabic corpus with known answer chunks (SRS D-hybrid decision):
/// recall@k for vector-only versus hybrid (vector + full text, reciprocal rank fusion), and the cosine scores that
/// guide the MinScore choice. Read the numbers from the test output; the assertions only guard against regressions.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetrievalQualityTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    private DocAgentApiFactory _factory = null!;
    private string _cs = null!;
    private TestUser _user = null!;

    public async Task InitializeAsync()
    {
        _ = ModelLocator.Directory;
        _cs = await TestDatabase.CreateAsync(postgres);
        _factory = new DocAgentApiFactory(_cs, new Dictionary<string, string?>
        {
            ["Documents:Ingestion:PollIntervalSeconds"] = "1",
            // Small chunks so that each corpus paragraph is (about) one chunk.
            ["Chunking:TargetTokens"] = "70",
            ["Chunking:MaxTokens"] = "110",
            ["Chunking:OverlapTokens"] = "0",
        });
        _user = await DocumentTestHelpers.CreateUserAsync(_factory);
        await DocumentTestHelpers.AddCredentialAsync(_cs, _user.Id); // BYOK limits: the corpus has more than 3 documents
        foreach (var doc in RetrievalCorpus.Documents)
        {
            var id = await _user.UploadOkAsync(Encoding.UTF8.GetBytes(string.Join("\n\n", doc.Paragraphs)), doc.FileName);
            Assert.Equal("Ready", (await _user.WaitForFinalStatusAsync(id)).GetProperty("status").GetString());
        }
    }

    public async Task DisposeAsync()
    {
        _user.Client.Dispose();
        await _factory.DisposeAsync();
    }

    private Retriever CreateRetriever(RetrievalOptions options) => new(
        TestDatabase.CreateContext(_cs, _user.Id),
        _factory.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
        Options.Create(options),
        NullLogger<Retriever>.Instance);

    private static readonly int[] Ks = [1, 3, 5, 8];

    private async Task<Dictionary<string, double[]>> RecallAsync(bool hybrid)
    {
        var retriever = CreateRetriever(new RetrievalOptions { Hybrid = hybrid, MinScore = 0, FullTextMinScore = 0, MaxContextTokens = int.MaxValue });
        var hits = new Dictionary<string, int[]>();
        var totals = new Dictionary<string, int>();
        var misses = new List<string>();
        foreach (var q in RetrievalCorpus.Questions)
        {
            var category = (q.Question.Any(c => c is >= '؀' and <= 'ۿ') ? "arabic" : "english") + (q.ExactTerm ? "-exact" : "-semantic");
            foreach (var key in new[] { category, "all" })
            {
                hits.TryAdd(key, new int[Ks.Length]);
                totals[key] = totals.GetValueOrDefault(key) + 1;
            }

            var results = await retriever.SearchAsync(q.Question, null, 8);
            var rank = results.ToList().FindIndex(r => r.Text.Contains(q.Marker));
            for (var i = 0; i < Ks.Length; i++)
            {
                if (rank >= 0 && rank < Ks[i])
                {
                    hits[category][i]++;
                    hits["all"][i]++;
                }
            }

            if (rank is < 0 or >= 3)
            {
                misses.Add($"    miss@3 [{(hybrid ? "hybrid" : "vector")}] rank={(rank < 0 ? "none" : (rank + 1).ToString())} q='{q.Question}'");
            }
        }

        misses.ForEach(output.WriteLine);
        return hits.ToDictionary(p => p.Key, p => p.Value.Select(h => (double)h / totals[p.Key]).ToArray());
    }

    [Fact]
    public async Task Recall_at_k_vector_only_versus_hybrid()
    {
        var vector = await RecallAsync(hybrid: false);
        var hybrid = await RecallAsync(hybrid: true);

        output.WriteLine($"questions: {RetrievalCorpus.Questions.Length}; k = {string.Join(", ", Ks)}");
        foreach (var key in vector.Keys.OrderBy(k => k))
        {
            output.WriteLine($"{key,-18} vector: {string.Join("  ", vector[key].Select((r, i) => $"@{Ks[i]}={r:P0}"))}");
            output.WriteLine($"{"",-18} hybrid: {string.Join("  ", hybrid[key].Select((r, i) => $"@{Ks[i]}={r:P0}"))}");
        }

        // Hybrid must not be worse than vector-only overall, and the exact-term queries are what it is for.
        Assert.True(hybrid["all"][2] >= vector["all"][2], "hybrid recall@5 fell below vector-only");
    }

    [Fact]
    public async Task Cosine_scores_of_answers_versus_unanswerable_questions()
    {
        var retriever = CreateRetriever(new RetrievalOptions { Hybrid = false, MinScore = 0, MaxContextTokens = int.MaxValue });

        var answerScores = new List<double>();
        foreach (var q in RetrievalCorpus.Questions)
        {
            var results = await retriever.SearchAsync(q.Question, null, 60);
            var target = results.FirstOrDefault(r => r.Text.Contains(q.Marker));
            answerScores.Add(target?.Score ?? 0);
            output.WriteLine($"answer {(q.ExactTerm ? "exact " : "sem   ")} target={target?.Score:F3} top1={results[0].Score:F3} q='{q.Question}'");
        }

        var noAnswerTop1 = new List<double>();
        foreach (var question in RetrievalCorpus.Unanswerable)
        {
            var results = await retriever.SearchAsync(question, null, 5);
            noAnswerTop1.Add(results[0].Score);
            output.WriteLine($"no-answer top1={results[0].Score:F3} q='{question}'");
        }

        answerScores.Sort();
        noAnswerTop1.Sort();
        output.WriteLine($"answer-chunk cosine: min={answerScores[0]:F3} p10={answerScores[answerScores.Count / 10]:F3} median={answerScores[answerScores.Count / 2]:F3}");
        output.WriteLine($"unanswerable top-1 cosine: median={noAnswerTop1[noAnswerTop1.Count / 2]:F3} max={noAnswerTop1[^1]:F3}");
    }
}
