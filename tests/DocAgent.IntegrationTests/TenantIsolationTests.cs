using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class TenantIsolationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Queries_only_return_rows_owned_by_the_current_user()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var (userA, docA) = await SeedUser(cs, "a@example.com");
        var (userB, docB) = await SeedUser(cs, "b@example.com");

        await using (var asA = TestDatabase.CreateContext(cs, userA))
        {
            Assert.Equal([docA], await asA.Documents.Select(d => d.Id).ToListAsync());
            Assert.Equal(docA, (await asA.Chunks.SingleAsync()).DocumentId);
            Assert.Equal(userA, (await asA.UserLlmCredentials.SingleAsync()).UserId);
            Assert.Equal(docA, (await asA.IngestionJobs.SingleAsync()).DocumentId);
            Assert.DoesNotContain(await asA.Documents.ToListAsync(), d => d.Id == docB);
        }

        await using (var asB = TestDatabase.CreateContext(cs, userB))
        {
            Assert.Equal([docB], await asB.Documents.Select(d => d.Id).ToListAsync());
            Assert.Equal(userB, (await asB.UserLlmCredentials.SingleAsync()).UserId);
        }

        await using var anonymous = TestDatabase.CreateContext(cs, null);
        Assert.Empty(await anonymous.Documents.ToListAsync());
        Assert.Empty(await anonymous.Chunks.ToListAsync());
        Assert.Empty(await anonymous.IngestionJobs.ToListAsync());
        Assert.Empty(await anonymous.UserLlmCredentials.ToListAsync());

        // Sanity: the data really is there for both users.
        Assert.Equal(2, await anonymous.Documents.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Vector_similarity_search_is_still_tenant_filtered()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var (userA, _) = await SeedUser(cs, "a@example.com");
        await SeedUser(cs, "b@example.com");

        await using var asA = TestDatabase.CreateContext(cs, userA);
        var query = new Vector(Enumerable.Repeat(0.5f, Chunk.EmbeddingDimensions).ToArray());
        var hits = await asA.Chunks
            .OrderBy(c => c.Embedding.CosineDistance(query))
            .Take(10)
            .ToListAsync();

        Assert.Single(hits);
        Assert.Equal(userA, hits[0].UserId);
    }

    private static async Task<(Guid UserId, Guid DocumentId)> SeedUser(string connectionString, string email)
    {
        await using var seed = TestDatabase.CreateContext(connectionString);
        var user = new AppUser { UserName = email, Email = email, NormalizedUserName = email.ToUpperInvariant(), NormalizedEmail = email.ToUpperInvariant() };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        // Writes through a context scoped to that user, as the app would.
        await using var db = TestDatabase.CreateContext(connectionString, user.Id);
        var doc = new Document
        {
            Id = Guid.NewGuid(), UserId = user.Id, FileName = "a.pdf", ContentType = "application/pdf",
            SizeBytes = 10, Status = DocumentStatus.Ready, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new Chunk
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, UserId = user.Id, Ordinal = 0, Text = "hello",
            TokenCount = 1, EmbeddingModel = "test",
            Embedding = new Vector(Enumerable.Repeat(0.5f, Chunk.EmbeddingDimensions).ToArray()),
        });
        db.IngestionJobs.Add(new IngestionJob
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, UserId = user.Id, State = IngestionJobState.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.UserLlmCredentials.Add(new UserLlmCredential
        {
            UserId = user.Id, Provider = LlmProvider.OpenRouter, Model = "m", EncryptedKey = "enc",
            KeyLast4 = "abcd", ValidatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (user.Id, doc.Id);
    }
}
