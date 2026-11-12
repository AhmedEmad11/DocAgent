using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Ingestion;
using DocAgent.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class IngestionRecoveryTests(PostgresFixture postgres)
{
    /// <summary>Seeds a user with a document, its staged bytes and a job in the given state.</summary>
    private static async Task<(Guid UserId, Guid DocumentId, Guid JobId)> SeedAsync(
        string cs, IngestionJobState state, int attempts, DocumentStatus documentStatus, string text = "A short note about recovery after a crash.")
    {
        await using var seed = TestDatabase.CreateContext(cs);
        var email = AuthTestHelpers.NewEmail();
        var user = new AppUser { UserName = email, Email = email, NormalizedUserName = email.ToUpperInvariant(), NormalizedEmail = email.ToUpperInvariant(), EmailConfirmed = true };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        await using var db = TestDatabase.CreateContext(cs, user.Id);
        var doc = new Document
        {
            Id = Guid.NewGuid(), UserId = user.Id, FileName = "stuck.txt", ContentType = "text/plain",
            SizeBytes = text.Length, Status = documentStatus, CreatedAt = DateTimeOffset.UtcNow,
        };
        var job = new IngestionJob
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, UserId = user.Id, State = state, Attempts = attempts,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        db.DocumentUploads.Add(new DocumentUpload { DocumentId = doc.Id, Content = System.Text.Encoding.UTF8.GetBytes(text) });
        db.IngestionJobs.Add(job);
        await db.SaveChangesAsync();
        return (user.Id, doc.Id, job.Id);
    }

    [Fact]
    public async Task Interrupted_running_job_is_requeued()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var (_, documentId, jobId) = await SeedAsync(cs, IngestionJobState.Running, 1, DocumentStatus.Processing);

        await using var db = TestDatabase.CreateContext(cs);
        var queue = new IngestionJobQueue(db, Options.Create(new IngestionOptions()));
        Assert.Equal(1, await queue.RecoverInterruptedAsync(default));

        var job = await db.IngestionJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(IngestionJobState.Queued, job.State);
        Assert.Equal(DocumentStatus.Pending, (await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == documentId)).Status);
        Assert.True(await db.DocumentUploads.AnyAsync(u => u.DocumentId == documentId)); // bytes kept for the retry
    }

    [Fact]
    public async Task Job_that_already_used_all_attempts_is_failed_and_its_bytes_are_discarded()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var (_, documentId, jobId) = await SeedAsync(cs, IngestionJobState.Running, 3, DocumentStatus.Processing);

        await using var db = TestDatabase.CreateContext(cs);
        await new IngestionJobQueue(db, Options.Create(new IngestionOptions())).RecoverInterruptedAsync(default);

        Assert.Equal(IngestionJobState.Failed, (await db.IngestionJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == jobId)).State);
        var doc = await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == documentId);
        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.Contains("interrupted", doc.FailureReason);
        Assert.False(await db.DocumentUploads.AnyAsync(u => u.DocumentId == documentId));
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_job()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        for (var i = 0; i < 6; i++)
        {
            await SeedAsync(cs, IngestionJobState.Queued, 0, DocumentStatus.Pending);
        }

        async Task<List<Guid>> Drain()
        {
            var claimed = new List<Guid>();
            while (true)
            {
                await using var db = TestDatabase.CreateContext(cs);
                var job = await new IngestionJobQueue(db, Options.Create(new IngestionOptions())).ClaimNextAsync(default);
                if (job is null)
                {
                    return claimed;
                }

                claimed.Add(job.JobId);
            }
        }

        var results = await Task.WhenAll(Drain(), Drain(), Drain());

        var all = results.SelectMany(r => r).ToList();
        Assert.Equal(6, all.Count);
        Assert.Equal(6, all.Distinct().Count());
    }

    [Fact]
    public async Task Worker_start_recovers_a_stuck_job_and_finishes_it()
    {
        _ = ModelLocator.Directory;
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var (userId, documentId, jobId) = await SeedAsync(cs, IngestionJobState.Running, 1, DocumentStatus.Processing);

        await using var factory = new DocAgentApiFactory(cs, new Dictionary<string, string?> { ["Documents:Ingestion:PollIntervalSeconds"] = "1" });
        using var client = factory.CreateClient(); // starts the host and the worker

        await WaitAsync(async () =>
        {
            await using var db = TestDatabase.CreateContext(cs);
            return (await db.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == documentId)).Status == DocumentStatus.Ready;
        });

        await using var check = TestDatabase.CreateContext(cs);
        var job = await check.IngestionJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(IngestionJobState.Succeeded, job.State);
        Assert.Equal(2, job.Attempts); // the interrupted run plus the resumed one
        Assert.True(await check.Chunks.IgnoreQueryFilters().AnyAsync(c => c.DocumentId == documentId && c.UserId == userId));
        Assert.False(await check.DocumentUploads.AnyAsync(u => u.DocumentId == documentId));
    }

    [Fact]
    public async Task Transient_errors_are_retried_and_then_succeed()
    {
        _ = ModelLocator.Directory;
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var flaky = new FlakyEmbeddingGenerator(failures: 1);
        await using var factory = new DocAgentApiFactory(cs, new Dictionary<string, string?> { ["Documents:Ingestion:PollIntervalSeconds"] = "1" },
            configureServices: services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(flaky));
        var user = await DocumentTestHelpers.CreateUserAsync(factory);

        var id = await user.UploadOkAsync("retry me please, this text is short"u8.ToArray(), "retry.txt");
        var doc = await user.WaitForFinalStatusAsync(id, TimeSpan.FromSeconds(60));

        Assert.Equal("Ready", doc.GetProperty("status").GetString());
        await using var db = TestDatabase.CreateContext(cs);
        Assert.Equal(2, (await db.IngestionJobs.IgnoreQueryFilters().SingleAsync(j => j.DocumentId == id)).Attempts);
        Assert.Equal(2, flaky.Calls);
    }

    [Fact]
    public async Task Persistent_transient_errors_fail_the_document_after_three_attempts()
    {
        _ = ModelLocator.Directory;
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        var flaky = new FlakyEmbeddingGenerator(failures: int.MaxValue);
        await using var factory = new DocAgentApiFactory(cs, new Dictionary<string, string?> { ["Documents:Ingestion:PollIntervalSeconds"] = "1" },
            configureServices: services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(flaky));
        var user = await DocumentTestHelpers.CreateUserAsync(factory);

        var id = await user.UploadOkAsync("this will never embed"u8.ToArray(), "never.txt");
        var doc = await user.WaitForFinalStatusAsync(id, TimeSpan.FromSeconds(90));

        Assert.Equal("Failed", doc.GetProperty("status").GetString());
        Assert.Contains("3 attempts", doc.GetProperty("failureReason").GetString());
        await using var db = TestDatabase.CreateContext(cs);
        var job = await db.IngestionJobs.IgnoreQueryFilters().SingleAsync(j => j.DocumentId == id);
        Assert.Equal(3, job.Attempts);
        Assert.Equal(IngestionJobState.Failed, job.State);
        Assert.Equal(nameof(InvalidOperationException), job.LastError); // type name only, never content
        Assert.False(await db.DocumentUploads.AnyAsync(u => u.DocumentId == id));
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached in time.");
            }

            await Task.Delay(250);
        }
    }

    private sealed class FlakyEmbeddingGenerator(int failures) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _calls;
        public int Calls => _calls;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) <= failures)
            {
                throw new InvalidOperationException("simulated embedding failure");
            }

            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
            {
                result.Add(new Embedding<float>(Enumerable.Repeat(0.05f, 384).ToArray()));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
