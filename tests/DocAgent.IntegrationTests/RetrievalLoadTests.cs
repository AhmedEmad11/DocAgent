using System.Diagnostics;
using System.Globalization;
using System.Text;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit.Abstractions;

namespace DocAgent.IntegrationTests;

/// <summary>Runs only when DOCAGENT_PERF_TESTS=1 (it seeds hundreds of thousands of rows and takes minutes).</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOCAGENT_PERF_TESTS") != "1")
        {
            Skip = "Set DOCAGENT_PERF_TESTS=1 to run the retrieval load test (PERF-2).";
        }
    }
}

/// <summary>
/// PERF-2: retrieval (embedding the query + vector search) within 1 s for a user with the maximum allowed chunks. The maximum is
/// 100 documents (BYOK limit, D19) x <c>Documents:Ingestion:MaxChunksPerDocument</c> (2,000) = 200,000 chunks for one user, plus other
/// users' chunks in the same table so that the tenant filter has real work to do. Vectors are synthetic (clustered, 384 dimensions, unit
/// length) and generated inside PostgreSQL; the query embedding is real (ONNX, in-process).
/// Environment: DOCAGENT_PERF_CHUNKS_PER_DOC (default 2000), DOCAGENT_PERF_DOCS (100), DOCAGENT_PERF_OTHER_CHUNKS (100000, spread over 10 users),
/// DOCAGENT_PERF_QUERIES (60), DOCAGENT_PERF_P95_MS (1000), DOCAGENT_PERF_OUTPUT (a file to append the report to).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetrievalLoadTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private readonly StringBuilder _report = new();

    private void Log(string line)
    {
        _report.AppendLine(line);
        output.WriteLine(line);
    }

    [PerfFact]
    public async Task Retrieval_at_the_maximum_chunks_per_user_stays_within_one_second()
    {
        _ = DocAgent.TestSupport.ModelLocator.Directory;
        var chunksPerDoc = Env("DOCAGENT_PERF_CHUNKS_PER_DOC", 2000);
        var docs = Env("DOCAGENT_PERF_DOCS", 100);
        var otherChunks = Env("DOCAGENT_PERF_OTHER_CHUNKS", 100_000);
        var queries = Env("DOCAGENT_PERF_QUERIES", 60);
        var targetMs = Env("DOCAGENT_PERF_P95_MS", 1000);
        var cs = await TestDatabase.CreateMigratedAsync(postgres);

        // ---- seed ----
        var seedTimer = Stopwatch.StartNew();
        var (target, others) = await CreateUsersAsync(cs, otherUsers: 10);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, """DROP INDEX "IX_Chunks_Embedding"; DROP INDEX "IX_Chunks_Text_fts";""");
            await ExecAsync(conn, """
                CREATE TABLE perf_centroids (id int PRIMARY KEY, a real[] NOT NULL);
                INSERT INTO perf_centroids SELECT c, ARRAY(SELECT random() - 0.5 FROM generate_series(1, 384) WHERE c >= 0) FROM generate_series(0, 199) c;
                """);
            await SeedUserAsync(conn, target, docs, chunksPerDoc);
            var perOther = Math.Max(1, otherChunks / others.Count);
            foreach (var other in others)
            {
                await SeedUserAsync(conn, other, docs: 10, chunksPerDoc: Math.Max(1, perOther / 10));
            }

            Log($"seeded in {seedTimer.Elapsed.TotalSeconds:F0} s (rows, no vector/full-text index yet)");
            var indexTimer = Stopwatch.StartNew();
            await ExecAsync(conn, "SET maintenance_work_mem = '1GB'; SET max_parallel_maintenance_workers = 0; -- parallel workers need more /dev/shm than the container has");
            await ExecAsync(conn, """CREATE INDEX "IX_Chunks_Embedding" ON "Chunks" USING hnsw ("Embedding" vector_cosine_ops);""");
            Log($"hnsw index built in {indexTimer.Elapsed.TotalSeconds:F0} s");
            indexTimer.Restart();
            await ExecAsync(conn, """CREATE INDEX "IX_Chunks_Text_fts" ON "Chunks" USING gin (to_tsvector('simple', "Text"));""");
            Log($"full-text index built in {indexTimer.Elapsed.TotalSeconds:F0} s");
            await ExecAsync(conn, """ANALYZE "Chunks"; ANALYZE "Documents";""");

            Log($"table: {await ScalarAsync(conn, """SELECT count(*) FROM "Chunks" """)} chunks in total, {await ScalarAsync(conn, $"""SELECT count(*) FROM "Chunks" WHERE "UserId" = '{target}' """)} owned by the test user, "
                + $"{await ScalarAsync(conn, "SELECT pg_size_pretty(pg_total_relation_size('\"Chunks\"'))")} with indexes (heap {await ScalarAsync(conn, "SELECT pg_size_pretty(pg_relation_size('\"Chunks\"'))")}, "
                + $"hnsw {await ScalarAsync(conn, "SELECT pg_size_pretty(pg_relation_size('\"IX_Chunks_Embedding\"'))")}, fts {await ScalarAsync(conn, "SELECT pg_size_pretty(pg_relation_size('\"IX_Chunks_Text_fts\"'))")})");
            Log("postgres: " + await ScalarAsync(conn, "SELECT version()") + "; shared_buffers=" + await ScalarAsync(conn, "SHOW shared_buffers"));
        }

        // ---- measure ----
        await using var factory = new DocAgentApiFactory(cs, new Dictionary<string, string?> { ["Documents:Ingestion:WorkerEnabled"] = "false" });
        var embedder = factory.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var rng = new Random(42);
        string Query() => string.Join(' ', Enumerable.Range(0, 3).Select(_ => "w" + rng.Next(0, 5000)));

        await embedder.GenerateAsync(["query: warm up"]); // load the ONNX session

        var embedOnly = await TimeAsync(queries, async () => await embedder.GenerateAsync(["query: " + Query() + " what is this about"]));
        Log($"query embedding alone: {embedOnly}");

        Retriever Create(bool hybrid) => new(TestDatabase.CreateContext(cs, target), embedder, Options.Create(new RetrievalOptions { Hybrid = hybrid, MinScore = 0, FullTextMinScore = 0 }), NullLogger<Retriever>.Instance);

        var first = Stopwatch.StartNew();
        await Create(false).SearchAsync(Query() + " what is this about", null, 8);
        Log($"first (cold) vector search after the index build: {first.ElapsedMilliseconds} ms");

        var vector = await TimeAsync(queries, async () => await Create(false).SearchAsync(Query() + " what is this about", null, 8));
        var hybridRun = await TimeAsync(queries, async () => await Create(true).SearchAsync(Query(), null, 8));
        var fiveDocs = (await QueryDocIdsAsync(cs, target, 5));
        var scoped = await TimeAsync(queries, async () => await Create(true).SearchAsync(Query(), fiveDocs, 8));
        var otherUser = await TimeAsync(Math.Min(queries, 20), async () =>
            await new Retriever(TestDatabase.CreateContext(cs, others[0]), embedder, Options.Create(new RetrievalOptions { Hybrid = true, MinScore = 0, FullTextMinScore = 0 }), NullLogger<Retriever>.Instance)
                .SearchAsync(Query(), null, 8));

        Log($"retrieval, vector only, all {docs} documents (embedding + search): {vector}");
        Log($"retrieval, hybrid (vector + full text), all documents:          {hybridRun}");
        Log($"retrieval, hybrid, restricted to 5 documents:                   {scoped}");
        Log($"retrieval, hybrid, a small user (10 docs) in the same table:    {otherUser}");

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            var qv = "[" + string.Join(',', Enumerable.Range(0, 384).Select(i => (i % 7 - 3) / 10.0).Select(d => d.ToString("F2", CultureInfo.InvariantCulture))) + "]";
            Log("--- EXPLAIN (ANALYZE, BUFFERS) of the vector query for the max user ---");
            Log(await ExplainAsync(conn, $"""
                EXPLAIN (ANALYZE, BUFFERS, COSTS OFF)
                SELECT c."Id", c."Embedding" <=> '{qv}'::vector AS d FROM "Chunks" c JOIN "Documents" doc ON doc."Id" = c."DocumentId"
                WHERE c."UserId" = '{target}' AND doc."UserId" = '{target}' AND doc."Status" = 'Ready'
                ORDER BY c."Embedding" <=> '{qv}'::vector LIMIT 32
                """));
        }

        var verdict = $"PERF-2 target: p95 <= {targetMs} ms. Results (p95): vector {vector.P95:F0} ms, hybrid {hybridRun.P95:F0} ms, scoped {scoped.P95:F0} ms.";
        Log(verdict);
        var path = Environment.GetEnvironmentVariable("DOCAGENT_PERF_OUTPUT");
        if (!string.IsNullOrEmpty(path))
        {
            await File.AppendAllTextAsync(path, $"=== {DateTime.UtcNow:u} ===\n{_report}\n");
        }

        Assert.True(vector.P95 <= targetMs && hybridRun.P95 <= targetMs && scoped.P95 <= targetMs, verdict);
    }

    private sealed record Timing(double P50, double P95, double Max, double Mean, int N)
    {
        public override string ToString() => $"p50 {P50:F0} ms, p95 {P95:F0} ms, max {Max:F0} ms, mean {Mean:F0} ms over {N} queries";
    }

    private static async Task<Timing> TimeAsync(int n, Func<Task> action)
    {
        for (var i = 0; i < 3; i++)
        {
            await action(); // warm-up, not measured
        }

        var times = new List<double>();
        for (var i = 0; i < n; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await action();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        times.Sort();
        double Percentile(double p) => times[Math.Min(times.Count - 1, (int)Math.Ceiling(p * times.Count) - 1)];
        return new Timing(Percentile(0.50), Percentile(0.95), times[^1], times.Average(), n);
    }

    private static async Task<(Guid Target, List<Guid> Others)> CreateUsersAsync(string cs, int otherUsers)
    {
        await using var db = TestDatabase.CreateContext(cs);
        var ids = new List<Guid>();
        for (var i = 0; i <= otherUsers; i++)
        {
            var email = $"perf{i}-{Guid.NewGuid():N}@example.com";
            var user = new AppUser { UserName = email, Email = email, NormalizedUserName = email.ToUpperInvariant(), NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true };
            db.Users.Add(user);
            ids.Add(user.Id);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var all = await db.Users.AsNoTracking().OrderBy(u => u.Email).Select(u => u.Id).ToListAsync();
        return (all[0], all.Skip(1).ToList());
    }

    /// <summary>Documents and chunks generated inside PostgreSQL: clustered unit vectors, about 1.2 KB of text from a 5,000-word vocabulary.</summary>
    private static async Task SeedUserAsync(NpgsqlConnection conn, Guid userId, int docs, int chunksPerDoc)
    {
        await ExecAsync(conn, $"""
            INSERT INTO "Documents" ("Id", "UserId", "FileName", "ContentType", "SizeBytes", "ChunkCount", "Status", "CreatedAt")
            SELECT gen_random_uuid(), '{userId}', 'doc-' || g || '.txt', 'text/plain', 1000000, {chunksPerDoc}, 'Ready', now()
            FROM generate_series(1, {docs}) g;

            INSERT INTO "Chunks" ("Id", "DocumentId", "UserId", "Ordinal", "Text", "TokenCount", "EmbeddingModel", "Embedding")
            SELECT gen_random_uuid(), d."Id", d."UserId", g.n,
                   (SELECT string_agg('w' || (floor(random() * 5000))::int, ' ') FROM generate_series(1, 200) k WHERE g.n >= 0),
                   380, 'perf',
                   l2_normalize(ARRAY(SELECT cen.a[i] + (random() - 0.5) * 0.5 FROM generate_series(1, 384) i WHERE g.n >= 0)::vector)
            FROM "Documents" d
            CROSS JOIN generate_series(0, {chunksPerDoc - 1}) g(n)
            JOIN perf_centroids cen ON cen.id = (abs(hashtext(d."Id"::text)) + g.n / 50) % 200
            WHERE d."UserId" = '{userId}';
            """);
    }

    private static async Task<List<Guid>> QueryDocIdsAsync(string cs, Guid userId, int count)
    {
        await using var db = TestDatabase.CreateContext(cs, userId);
        return await db.Documents.Select(d => d.Id).Take(count).ToListAsync();
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? "";
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync();
        var sb = new StringBuilder();
        while (await reader.ReadAsync())
        {
            sb.AppendLine(reader.GetString(0));
        }

        return sb.ToString();
    }
}
