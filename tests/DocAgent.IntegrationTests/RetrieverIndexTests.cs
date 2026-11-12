using System.Data.Common;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Xunit.Abstractions;

namespace DocAgent.IntegrationTests;

/// <summary>Checks with EXPLAIN that the retriever's ORDER BY ... &lt;=&gt; query can use the HNSW index, with another tenant's rows present.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RetrieverIndexTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private const int ChunksPerUser = 1500;

    [Fact]
    public async Task Vector_query_can_use_the_hnsw_index_with_tenant_filters_and_stays_tenant_scoped()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var random = new Random(7);
        var userA = await SeedAsync(cs, "a@example.com", random);
        await SeedAsync(cs, "b@example.com", random);
        await using (var admin = TestDatabase.CreateContext(cs))
        {
            await admin.Database.ExecuteSqlRawAsync("ANALYZE \"Chunks\"; ANALYZE \"Documents\";");
        }

        var plans = new List<string>();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs, o => o.UseVector())
            .AddInterceptors(new ExplainInterceptor(plans))
            .Options;
        await using var db = new AppDbContext(options, new FixedUserForTest(userA));
        await db.Database.OpenConnectionAsync(); // keep one connection so the SET below applies to the retriever's queries

        var retriever = new Retriever(db, new RandomEmbeddingGenerator(random), Options.Create(new RetrievalOptions { Hybrid = false, MinScore = -1 }), NullLogger<Retriever>.Instance);

        // 1. Planner's own choice at this table size.
        var natural = await retriever.SearchAsync("anything", null, 8);
        output.WriteLine("--- default planner settings ---");
        plans.ForEach(output.WriteLine);

        // 2. With sequential scans disabled the planner shows whether the HNSW index can serve this exact query shape.
        plans.Clear();
        await db.Database.ExecuteSqlRawAsync("SET enable_seqscan = off");
        var forced = await retriever.SearchAsync("anything", null, 8);
        output.WriteLine("--- enable_seqscan = off ---");
        plans.ForEach(output.WriteLine);

        // 3. The planner prefers the btree on UserId (exact search over one tenant's rows) at this size. Take that
        //    alternative away and check that the same query, filters included, is served by the HNSW index.
        plans.Clear();
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_Chunks_UserId\"");
        var viaIndex = await retriever.SearchAsync("anything", null, 8);
        output.WriteLine("--- enable_seqscan = off, IX_Chunks_UserId dropped ---");
        plans.ForEach(output.WriteLine);
        Assert.Contains(plans, p => p.Contains("Index Scan using \"IX_Chunks_Embedding\""));
        Assert.Equal(8, viaIndex.Count); // iterative scan: the tenant filter does not starve the result
        Assert.All(natural.Concat(forced), r => Assert.NotEqual(Guid.Empty, r.DocumentId));
        Assert.Equal(8, forced.Count);

        // Only user A's chunks exist for A, even though B has the same number of rows nearby.
        var owners = await TestDatabase.CreateContext(cs).Chunks.IgnoreQueryFilters()
            .Where(c => forced.Select(f => f.ChunkId).Contains(c.Id)).Select(c => c.UserId).Distinct().ToListAsync();
        Assert.Equal([userA], owners);
    }

    private static async Task<Guid> SeedAsync(string cs, string email, Random random)
    {
        await using var seed = TestDatabase.CreateContext(cs);
        var user = new AppUser { UserName = email, Email = email, NormalizedUserName = email.ToUpperInvariant(), NormalizedEmail = email.ToUpperInvariant() };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        await using var db = TestDatabase.CreateContext(cs, user.Id);
        for (var d = 0; d < 15; d++)
        {
            var doc = new Document
            {
                Id = Guid.NewGuid(), UserId = user.Id, FileName = $"doc{d}.txt", ContentType = "text/plain", SizeBytes = 1,
                Status = DocumentStatus.Ready, CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Documents.Add(doc);
            for (var i = 0; i < ChunksPerUser / 15; i++)
            {
                db.Chunks.Add(new Chunk
                {
                    Id = Guid.NewGuid(), DocumentId = doc.Id, UserId = user.Id, Ordinal = i, Text = $"chunk {d}-{i}",
                    TokenCount = 5, EmbeddingModel = "test", Embedding = RandomEmbeddingGenerator.RandomVector(random),
                });
            }
        }

        await db.SaveChangesAsync();
        return user.Id;
    }

    private sealed class FixedUserForTest(Guid id) : DocAgent.Core.Abstractions.ICurrentUser
    {
        public Guid? UserId => id;
    }

    private sealed class RandomEmbeddingGenerator(Random random) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public static Vector RandomVector(Random random)
        {
            var values = new float[Chunk.EmbeddingDimensions];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = (float)(random.NextDouble() - 0.5);
            }

            return new Vector(values);
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(RandomVector(random).ToArray()))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ExplainInterceptor(List<string> plans) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("<=>"))
            {
                await using var explain = command.Connection!.CreateCommand();
                explain.Transaction = command.Transaction;
                explain.CommandText = "EXPLAIN " + command.CommandText;
                foreach (NpgsqlParameter p in command.Parameters)
                {
                    explain.Parameters.Add(p.Clone());
                }

                await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
                var lines = new List<string>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    lines.Add(reader.GetString(0));
                }

                plans.Add(System.Text.RegularExpressions.Regex.Replace(string.Join(Environment.NewLine, lines), @"'\[[^\]]*\]'", "'[vector]'"));
            }

            return result;
        }
    }
}
