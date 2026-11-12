using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace DocAgent.IntegrationTests;

/// <summary>Runs only when DOCAGENT_OLLAMA_TESTS=1: needs Ollama on localhost with the configured model.</summary>
public sealed class OllamaFactAttribute : FactAttribute
{
    public OllamaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOCAGENT_OLLAMA_TESTS") != "1")
        {
            Skip = "Set DOCAGENT_OLLAMA_TESTS=1 to run tests against a local Ollama (gemma4:12b).";
        }
    }
}

/// <summary>
/// End-to-end Q&amp;A with the real model: real ONNX embeddings, real Postgres, real Ollama (no fake chat client).
/// Optional env: DOCAGENT_OLLAMA_THINK=true|false overrides Llm:Ollama:Think (default false); DOCAGENT_OLLAMA_ENDPOINT / _MODEL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OllamaRealModelTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private static readonly string[] EnglishDocs = ["handbook.txt", "router-manual.txt", "trial-summary.txt"];
    private static readonly string[] ArabicDocs = ["سياسة-الإجازات.txt", "دليل-الجهاز.txt"];

    private sealed record Case(string Question, string DocumentFile, string[] AnyOf);

    private static readonly Case[] Cases =
    [
        new("How many days of paid vacation do full-time employees get per year?", "handbook.txt", ["25"]),
        new("What is the web address of the router administration page?", "router-manual.txt", ["192.168.7.1"]),
        new("Who was the principal investigator of the clinical study?", "trial-summary.txt", ["Halvorsen"]),
        new("كم يوما من الإجازة السنوية يحصل عليها الموظف بدوام كامل؟", "سياسة-الإجازات.txt", ["ثلاثين", "30", "٣٠"]),
        new("ما مدة الضمان على جهاز تنقية المياه؟", "دليل-الجهاز.txt", ["ثلاث سنوات", "3 سنوات", "٣ سنوات", "ثلاثة"]),
    ];

    [OllamaFact]
    public async Task Answers_questions_in_english_and_arabic_with_the_right_citations_and_admits_when_it_does_not_know()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Documents:Ingestion:PollIntervalSeconds"] = "1",
            ["Retrieval:MinScore"] = "0", // let the unanswerable question reach the model, to test its own "not found"
        };
        foreach (var (env, key) in new[] { ("DOCAGENT_OLLAMA_THINK", "Llm:Ollama:Think"), ("DOCAGENT_OLLAMA_ENDPOINT", "Llm:Ollama:Endpoint"), ("DOCAGENT_OLLAMA_MODEL", "Llm:Ollama:Model") })
        {
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } value)
            {
                settings[key] = value;
            }
        }

        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(cs, settings); // real Ollama chat client
        var user = await DocumentTestHelpers.CreateUserAsync(factory);
        await DocumentTestHelpers.AddCredentialAsync(cs, user.Id);

        var ids = new Dictionary<string, Guid>();
        foreach (var doc in RetrievalCorpus.Core.Where(d => EnglishDocs.Contains(d.FileName) || ArabicDocs.Contains(d.FileName)))
        {
            ids[doc.FileName] = await user.UploadOkAsync(Encoding.UTF8.GetBytes(string.Join("\n\n", doc.Paragraphs)), doc.FileName);
        }

        foreach (var id in ids.Values)
        {
            Assert.Equal("Ready", (await user.WaitForFinalStatusAsync(id)).GetProperty("status").GetString());
        }

        var failures = new List<string>();
        foreach (var c in Cases)
        {
            var (body, elapsed) = await AskAsync(user, c.Question);
            var answer = body.GetProperty("answer").GetString()!;
            var citedDocs = body.GetProperty("citations").EnumerateArray().Select(x => x.GetProperty("documentId").GetGuid()).ToList();
            var containsFact = c.AnyOf.Any(answer.Contains);
            var citesRight = citedDocs.Contains(ids[c.DocumentFile]);
            output.WriteLine($"[{(containsFact && citesRight ? "PASS" : "FAIL")}] {elapsed.TotalSeconds,5:F1}s q='{c.Question}'\n    answer={answer}\n    cites={string.Join(", ", body.GetProperty("citations").EnumerateArray().Select(x => $"[{x.GetProperty("index").GetInt32()}] {x.GetProperty("fileName").GetString()}"))} notFound={body.GetProperty("notFound").GetBoolean()} model={body.GetProperty("model").GetString()}");
            if (!containsFact)
            {
                failures.Add($"answer lacks the expected fact: {c.Question}");
            }

            if (!citesRight)
            {
                failures.Add($"does not cite {c.DocumentFile}: {c.Question}");
            }
        }

        // A question the documents cannot answer: sources are weakly related (MinScore 0), the model must say so.
        const string unanswerable = "What is the maximum amount of sick leave an employee can take per year?";
        var (nf, nfElapsed) = await AskAsync(user, unanswerable);
        output.WriteLine($"[{(nf.GetProperty("notFound").GetBoolean() ? "PASS" : "FAIL")}] {nfElapsed.TotalSeconds,5:F1}s unanswerable q='{unanswerable}'\n    answer={nf.GetProperty("answer").GetString()} notFound={nf.GetProperty("notFound").GetBoolean()} citations={nf.GetProperty("citations").GetArrayLength()}");
        if (!nf.GetProperty("notFound").GetBoolean())
        {
            failures.Add("the model invented an answer to an unanswerable question");
        }

        Assert.Empty(failures);
    }

    private static async Task<(JsonElement Body, TimeSpan Elapsed)> AskAsync(TestUser user, string question)
    {
        var started = Stopwatch.StartNew();
        var response = await user.Client.PostAsJsonAsync("/api/v1/ask", new { question });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return (body, started.Elapsed);
    }
}
