using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocAgent.IntegrationTests;
using Xunit.Abstractions;

namespace DocAgent.EvalTests;

/// <summary>Runs only when DOCAGENT_OLLAMA_TESTS=1: needs Ollama on localhost with the configured model.</summary>
public sealed class OllamaEvalFactAttribute : FactAttribute
{
    public OllamaEvalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOCAGENT_OLLAMA_TESTS") != "1")
        {
            Skip = "Set DOCAGENT_OLLAMA_TESTS=1 to run the agent evaluation against a local Ollama (gemma4:12b).";
        }
    }
}

/// <summary>
/// SRS D32: the English + Arabic evaluation set against the real model. Real ONNX embeddings, real Postgres (Testcontainers),
/// real Ollama. Targets: at least 80% of the answerable questions correct (all key facts plus a citation to an expected
/// document) and 100% of the unanswerable ones answered as not found without citations.
/// Optional env: DOCAGENT_EVAL_ONLY=id1,id2 (or id prefixes such as "ar-"), DOCAGENT_EVAL_MODE=SinglePass|Agent,
/// DOCAGENT_EVAL_OUT=path of the results file, DOCAGENT_OLLAMA_ENDPOINT / _MODEL / _THINK.
/// </summary>
public sealed class AgentEvalTests(ITestOutputHelper output)
{
    private sealed record Row(EvalItem Item, Grade Grade, double Seconds, int ToolCalls, bool Incomplete, string Answer, IReadOnlyList<string> Cited);

    [OllamaEvalFact]
    public async Task Evaluation_set_meets_the_d32_targets()
    {
        var outPath = Environment.GetEnvironmentVariable("DOCAGENT_EVAL_OUT") is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "eval-results.md");
        var progressPath = outPath + ".progress.log";
        File.WriteAllText(progressPath, "");
        void Log(string line)
        {
            output.WriteLine(line);
            File.AppendAllText(progressPath, line + "\n");
        }

        var set = EvalSetFile.Load(Path.Combine(AppContext.BaseDirectory, "eval-set.json"));
        var only = Environment.GetEnvironmentVariable("DOCAGENT_EVAL_ONLY")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var items = set.Items.Where(i => only is null || only.Any(o => i.Id.StartsWith(o, StringComparison.Ordinal))).ToList();

        var settings = new Dictionary<string, string?> { ["Documents:Ingestion:PollIntervalSeconds"] = "1" };
        foreach (var (env, key) in new[]
        {
            ("DOCAGENT_OLLAMA_THINK", "Llm:Ollama:Think"), ("DOCAGENT_OLLAMA_ENDPOINT", "Llm:Ollama:Endpoint"),
            ("DOCAGENT_OLLAMA_MODEL", "Llm:Ollama:Model"), ("DOCAGENT_EVAL_MODE", "Qa:Mode"),
        })
        {
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } value)
            {
                settings[key] = value;
            }
        }

        await using var postgres = new PostgresFixtureHandle();
        var cs = await TestDatabase.CreateAsync(postgres.Fixture);
        await using var factory = new DocAgentApiFactory(cs, settings); // real Ollama chat client, default limits
        var seed = await DocumentTestHelpers.CreateUserAsync(factory);
        await DocumentTestHelpers.AddCredentialAsync(cs, seed.Id); // BYOK tier: the demo tier allows only 3 documents
        using var anonymous = factory.CreateClient();
        using var client = factory.CreateClient().WithBearer((await anonymous.LoginAsync(seed.Email)).AccessToken);
        client.Timeout = TimeSpan.FromMinutes(6);
        var user = new TestUser(client, seed.Id, seed.Email);

        var ids = new Dictionary<string, Guid>();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Corpus")).OrderBy(f => f, StringComparer.Ordinal))
        {
            ids[Path.GetFileName(file)] = await user.UploadOkAsync(await File.ReadAllBytesAsync(file), Path.GetFileName(file));
        }

        foreach (var (name, id) in ids)
        {
            Assert.Equal("Ready", (await user.WaitForFinalStatusAsync(id)).GetProperty("status").GetString());
        }

        await WarmUpAsync(settings);
        Log($"corpus: {ids.Count} documents; items: {items.Count}; mode: {settings.GetValueOrDefault("Qa:Mode") ?? "Agent"}");
        var rows = new List<Row>();
        foreach (var item in items)
        {
            var started = Stopwatch.StartNew();
            ApiAnswer api;
            try
            {
                api = await CallAsync(client, item, ids);
            }
            catch (Exception ex)
            {
                var failed = new Row(item, new Grade(false, "request failed: " + ex.GetType().Name + " " + ex.Message), started.Elapsed.TotalSeconds, 0, false, "", []);
                rows.Add(failed);
                Log($"{item.Id,-10} FAIL {started.Elapsed.TotalSeconds,6:F1}s  {failed.Grade.Reason}");
                continue;
            }

            var grade = Grader.Grade(item, api);
            rows.Add(new Row(item, grade, started.Elapsed.TotalSeconds, api.ToolCalls, api.Incomplete, api.Answer, api.CitedFiles));
            Log($"{item.Id,-10} {(grade.Passed ? "PASS" : "FAIL")} {started.Elapsed.TotalSeconds,6:F1}s calls={api.ToolCalls} {(grade.Passed ? "" : grade.Reason + " | ")}answer={api.Answer.Replace('\n', ' ')[..Math.Min(260, api.Answer.Length)]}");
        }

        if (only is null || Environment.GetEnvironmentVariable("DOCAGENT_EVAL_STREAM") == "1")
        {
            Log(await StreamCheckAsync(client));
            Log(await StreamCheckAsync(client)); // second time: the first includes any one-off warm-up
        }

        var report = BuildReport(rows);
        await File.WriteAllTextAsync(outPath, report);
        await File.WriteAllTextAsync(outPath + ".answers.json", JsonSerializer.Serialize(
            rows.Select(r => new { r.Item.Id, r.Grade.Passed, r.Grade.Reason, r.Seconds, r.ToolCalls, r.Incomplete, r.Cited, r.Answer }),
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Log(report);

        var answerable = rows.Where(r => r.Item.Answerable).ToList();
        var unanswerable = rows.Where(r => !r.Item.Answerable).ToList();
        if (only is null)
        {
            Assert.True(answerable.Count(r => r.Grade.Passed) * 100 >= answerable.Count * 80,
                $"Answerable correct: {answerable.Count(r => r.Grade.Passed)}/{answerable.Count} (target 80%)");
            Assert.True(unanswerable.All(r => r.Grade.Passed),
                "Unanswerable questions not answered as not found: " + string.Join(", ", unanswerable.Where(r => !r.Grade.Passed).Select(r => r.Item.Id)));
        }
    }

    /// <summary>Loads the model into memory first, so the first question's latency does not include the load.</summary>
    private static async Task WarmUpAsync(Dictionary<string, string?> settings)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var endpoint = settings.GetValueOrDefault("Llm:Ollama:Endpoint") ?? "http://localhost:11434";
        var model = settings.GetValueOrDefault("Llm:Ollama:Model") ?? "gemma4:12b";
        using var response = await http.PostAsJsonAsync(endpoint.TrimEnd('/') + "/api/chat", new
        {
            model,
            messages = new[] { new { role = "user", content = "Hi" } },
            stream = false,
            think = false,
            keep_alive = "30m",
            options = new { num_ctx = 8192, num_predict = 1 }, // the same context size as the API requests, or Ollama reloads the model
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>One question through /ask/stream with the real model: the event sequence must end in an answer.</summary>
    private static async Task<string> StreamCheckAsync(HttpClient client)
    {
        var started = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync("/api/v1/ask/stream", new { question = "How many days of paid vacation do full-time employees get per year?" });
        var text = await response.Content.ReadAsStringAsync();
        var names = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(b => b.Split('\n')[0].Replace("event: ", "")).ToList();
        Assert.Equal("answer", names[^1]);
        Assert.Equal("searching", names[0]);
        return $"SSE check: {string.Join(" > ", names)} in {started.Elapsed.TotalSeconds:F1}s";
    }

    private static async Task<ApiAnswer> CallAsync(HttpClient client, EvalItem item, Dictionary<string, Guid> ids)
    {
        HttpResponseMessage response = item.Type switch
        {
            "summarize" => await client.PostAsJsonAsync($"/api/v1/documents/{ids[item.Document!]}/summary", new { focus = item.Focus }),
            "compare" => await client.PostAsJsonAsync("/api/v1/documents/compare",
                new { documentIdA = ids[item.DocumentA!], documentIdB = ids[item.DocumentB!], focus = item.Focus }),
            _ => await client.PostAsJsonAsync("/api/v1/ask", new { question = item.Question }),
        };
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode}: {body.GetRawText()[..Math.Min(200, body.GetRawText().Length)]}");
        }

        var cited = body.GetProperty("citations").EnumerateArray().Select(c => c.GetProperty("fileName").GetString()!).ToList();
        return new ApiAnswer(
            body.GetProperty("answer").GetString() ?? "", body.GetProperty("notFound").GetBoolean(), cited,
            body.TryGetProperty("incomplete", out var inc) && inc.GetBoolean(),
            body.TryGetProperty("toolCalls", out var calls) ? calls.GetInt32() : 0);
    }

    private static string BuildReport(List<Row> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| id | lang | type | result | latency (s) | tool calls | note |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            sb.AppendLine($"| {r.Item.Id} | {r.Item.Lang} | {r.Item.Type} | {(r.Grade.Passed ? "pass" : "FAIL")} | {r.Seconds:F1} | {r.ToolCalls} | {(r.Grade.Passed ? (r.Incomplete ? "incomplete" : "") : r.Grade.Reason)} |");
        }

        var answerable = rows.Where(r => r.Item.Answerable).ToList();
        var unanswerable = rows.Where(r => !r.Item.Answerable).ToList();
        string Pct(int a, int b) => b == 0 ? "n/a" : $"{a * 100.0 / b:F0}%";
        sb.AppendLine();
        sb.AppendLine($"Answerable correct: {answerable.Count(r => r.Grade.Passed)}/{answerable.Count} ({Pct(answerable.Count(r => r.Grade.Passed), answerable.Count)}); target >= 80%");
        sb.AppendLine($"Unanswerable answered as not found: {unanswerable.Count(r => r.Grade.Passed)}/{unanswerable.Count} ({Pct(unanswerable.Count(r => r.Grade.Passed), unanswerable.Count)}); target 100%");
        foreach (var lang in new[] { "en", "ar" })
        {
            var group = answerable.Where(r => r.Item.Lang == lang).ToList();
            sb.AppendLine($"  answerable {lang}: {group.Count(r => r.Grade.Passed)}/{group.Count}");
        }

        sb.AppendLine();
        sb.AppendLine("Latency by type (avg s / max s / avg tool calls):");
        foreach (var g in rows.GroupBy(r => r.Item.Type).OrderBy(g => g.Key))
        {
            sb.AppendLine($"  {g.Key,-14} n={g.Count(),2}  {g.Average(r => r.Seconds):F1} / {g.Max(r => r.Seconds):F1} / {g.Average(r => r.ToolCalls):F1}");
        }

        return sb.ToString();
    }

    /// <summary>Starts a PostgreSQL container on demand, so the skipped default run never touches Docker.</summary>
    private sealed class PostgresFixtureHandle : IAsyncDisposable
    {
        public PostgresFixture Fixture { get; } = new();

        public PostgresFixtureHandle() => Fixture.InitializeAsync().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync() => await Fixture.DisposeAsync();
    }
}
