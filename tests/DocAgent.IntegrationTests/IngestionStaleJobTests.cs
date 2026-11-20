using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocAgent.IntegrationTests;

/// <summary>REL-3 while the process stays up: a job whose worker died or whose failure could not be written is taken back after a lease.</summary>
[Collection(PostgresCollection.Name)]
public sealed class IngestionStaleJobTests(PostgresFixture postgres)
{
    private static async Task<Guid> SeedRunningJobAsync(string cs, Guid userId, TimeSpan idleFor, int attempts)
    {
        await using var db = TestDatabase.CreateContext(cs, userId);
        var doc = new Document
        {
            Id = Guid.NewGuid(), UserId = userId, FileName = "stuck.txt", ContentType = "text/plain", SizeBytes = 10,
            Status = DocumentStatus.Processing, CreatedAt = DateTimeOffset.UtcNow,
        };
        var job = new IngestionJob
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, UserId = userId, State = IngestionJobState.Running, Attempts = attempts,
            CreatedAt = DateTimeOffset.UtcNow - idleFor, UpdatedAt = DateTimeOffset.UtcNow - idleFor,
        };
        db.Documents.Add(doc);
        db.DocumentUploads.Add(new DocumentUpload { DocumentId = doc.Id, Content = "Some staged bytes."u8.ToArray() });
        db.IngestionJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    [Fact]
    public async Task Only_jobs_idle_longer_than_the_lease_are_taken_back()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        await using (var seed = TestDatabase.CreateContext(cs))
        {
            var email = AuthTestHelpers.NewEmail();
            seed.Users.Add(new AppUser { UserName = email, Email = email, NormalizedUserName = email.ToUpperInvariant(), NormalizedEmail = email.ToUpperInvariant() });
            await seed.SaveChangesAsync();
        }

        Guid userId;
        await using (var look = TestDatabase.CreateContext(cs))
        {
            userId = (await look.Users.SingleAsync()).Id;
        }

        var stale = await SeedRunningJobAsync(cs, userId, TimeSpan.FromHours(1), attempts: 1);
        var staleOutOfAttempts = await SeedRunningJobAsync(cs, userId, TimeSpan.FromHours(1), attempts: 3);
        var live = await SeedRunningJobAsync(cs, userId, TimeSpan.FromSeconds(30), attempts: 1);

        await using var db = TestDatabase.CreateContext(cs);
        var queue = new IngestionJobQueue(db, Options.Create(new IngestionOptions()));
        var changed = await queue.RecoverInterruptedAsync(default, TimeSpan.FromMinutes(30));

        Assert.Equal(2, changed);
        async Task<IngestionJobState> StateOf(Guid id) => (await db.IngestionJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == id)).State;
        Assert.Equal(IngestionJobState.Queued, await StateOf(stale));
        Assert.Equal(IngestionJobState.Failed, await StateOf(staleOutOfAttempts));
        Assert.Equal(IngestionJobState.Running, await StateOf(live)); // another instance's live job is left alone
    }
}
