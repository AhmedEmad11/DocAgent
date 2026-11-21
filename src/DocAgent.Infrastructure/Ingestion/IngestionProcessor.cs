using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;

namespace DocAgent.Infrastructure.Ingestion;

public enum IngestionOutcome { Succeeded, Failed, WillRetry, DocumentGone }

/// <summary>
/// Runs one claimed job: extract, chunk, embed, store, then discard the uploaded bytes (SRS D15).
/// Runs without an HTTP user, so every query that touches tenant data uses IgnoreQueryFilters explicitly and is
/// scoped by the document id of the claimed job. Never logs document content.
/// </summary>
public sealed class IngestionProcessor(
    AppDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    TextChunker chunker,
    IOptions<IngestionOptions> options,
    IOptions<DocumentLimitsOptions> limitOptions,
    ILogger<IngestionProcessor> logger)
{
    /// <summary>Stored on documents whose PDF Arabic had to be reconstructed from reversed order (SRS DOC-11 / D31).</summary>
    public const string ArabicReorderedNotice =
        "Arabic text in this PDF was stored in reversed order and was reconstructed automatically; some Arabic words or diacritics may be inaccurate.";

    private static readonly TimeSpan TimeoutGrace = TimeSpan.FromSeconds(5);

    public async Task<IngestionOutcome> ProcessAsync(ClaimedJob job, CancellationToken ct)
    {
        var documentId = job.DocumentId;
        var document = await db.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.UserId, d.ContentType, d.SizeBytes })
            .FirstOrDefaultAsync(ct);
        if (document is null)
        {
            logger.LogInformation("Ingestion job {JobId}: document {DocumentId} was deleted; skipping.", job.JobId, documentId);
            return IngestionOutcome.DocumentGone;
        }

        await db.Documents.IgnoreQueryFilters().Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DocumentStatus.Processing), ct);

        try
        {
            var type = DetectedFileTypeExtensions.FromContentType(document.ContentType);
            var (chunks, pageCount, arabicReordered) = await LoadExtractAndChunkAsync(documentId, document.SizeBytes, type, ct);
            if (chunks.Count == 0)
            {
                throw new NoExtractableTextException("The document contains no text.");
            }

            if (chunks.Count > options.Value.MaxChunksPerDocument)
            {
                // Before embedding: a document this long must not cost the CPU time or the storage.
                throw new DocumentParseException(
                    $"The document is too long to index: it has more than {options.Value.MaxChunksPerDocument} text sections. Split it into smaller documents.");
            }

            // Early check, before the embedding cost. Not race-safe on its own; StoreResultAsync repeats it under the user's lock.
            var totalLimit = await TotalChunkLimitAsync(document.UserId, ct);
            if (totalLimit > 0)
            {
                var used = await UsedChunksAsync(document.UserId, documentId, ct);
                if (used + chunks.Count > totalLimit)
                {
                    throw new DocumentParseException(StorageLimitMessage(used, totalLimit, chunks.Count));
                }
            }

            var inputs = chunks.Select(c => EmbeddingOptions.PassagePrefix + c.Text).ToList();
            var embeddings = await embedder.GenerateAsync(inputs, cancellationToken: ct);

            if (!await StoreResultAsync(job, document.UserId, totalLimit, chunks, embeddings, pageCount, arabicReordered ? ArabicReorderedNotice : null, ct))
            {
                return IngestionOutcome.DocumentGone;
            }

            logger.LogInformation(
                "Ingestion job {JobId}: document {DocumentId} is ready with {ChunkCount} chunks (managed heap {ManagedMiB} MiB, process working set {WorkingSetMiB} MiB).",
                job.JobId, documentId, chunks.Count, GC.GetTotalMemory(false) / (1024 * 1024), Environment.WorkingSet / (1024 * 1024));
            return IngestionOutcome.Succeeded;
        }
        catch (DocumentParseException ex)
        {
            await FailAsync(job, ex.Message, ct);
            logger.LogWarning("Ingestion job {JobId}: document {DocumentId} failed: {Reason}", job.JobId, documentId, ex.Message);
            return IngestionOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down (a host that puts an idle container to sleep, a redeploy): give the job back to the queue right away and do not count the
            // interrupted run as an attempt. If this write cannot happen either (killed, database gone) the job stays Running and REL-3 recovers it at the next start.
            await ReleaseAsync(job);
            throw;
        }
        catch (Exception ex)
        {
            if (!await db.Documents.IgnoreQueryFilters().AnyAsync(d => d.Id == documentId, CancellationToken.None))
            {
                return IngestionOutcome.DocumentGone;
            }

            if (job.Attempts >= options.Value.MaxAttempts)
            {
                var reason = $"Processing failed after {job.Attempts} attempts because of a temporary error. Please upload the file again.";
                await FailAsync(job, reason, CancellationToken.None, lastError: ex.GetType().Name);
                logger.LogError(ex, "Ingestion job {JobId}: document {DocumentId} failed permanently after {Attempts} attempts.", job.JobId, documentId, job.Attempts);
                return IngestionOutcome.Failed;
            }

            await RequeueAsync(job, ex.GetType().Name, CancellationToken.None);
            logger.LogWarning(ex, "Ingestion job {JobId}: document {DocumentId} hit a temporary error on attempt {Attempts}; will retry.", job.JobId, documentId, job.Attempts);
            return IngestionOutcome.WillRetry;
        }
    }

    /// <summary>
    /// Reads the staged file, extracts and chunks it, and lets go of the file bytes before the (long) embedding starts. The bytes are read with a
    /// sequential reader straight into one array of the known size: the default row buffering would hold the whole 25 MB file twice, and on a 512 MB
    /// container that copy is worth more than a fifth of the memory. The array is collected (large-object heap compacted) as soon as the text is out.
    /// </summary>
    private async Task<(IReadOnlyList<TextChunk> Chunks, int? PageCount, bool ArabicReordered)> LoadExtractAndChunkAsync(
        Guid documentId, long sizeBytes, DetectedFileType type, CancellationToken ct)
    {
        var content = await LoadContentAsync(documentId, sizeBytes, ct)
            ?? throw new DocumentParseException("The uploaded file is no longer available. Please upload it again.");
        var size = content.Length;
        var result = await ExtractAndChunkAsync(type, content, ct);
        content = null;
        if (size > 1024 * 1024)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        return result;
    }

    private async Task<byte[]?> LoadContentAsync(Guid documentId, long sizeBytes, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = (Npgsql.NpgsqlCommand)db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT \"Content\" FROM \"DocumentUploads\" WHERE \"DocumentId\" = @id";
            command.Parameters.AddWithValue("id", documentId);
            await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess | System.Data.CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            await using var stream = reader.GetStream(0);
            if (sizeBytes is > 0 and <= int.MaxValue)
            {
                var exact = new byte[sizeBytes];
                var read = 0;
                while (read < exact.Length)
                {
                    var n = await stream.ReadAsync(exact.AsMemory(read), ct);
                    if (n == 0) { break; }
                    read += n;
                }

                if (read == exact.Length && await stream.ReadAsync(new byte[1], ct) == 0)
                {
                    return exact;
                }

                // The recorded size does not match the stored bytes (should not happen): fall back to a plain copy below.
                await using var again = new MemoryStream(exact, 0, read);
                var rest = new MemoryStream();
                await again.CopyToAsync(rest, ct);
                await stream.CopyToAsync(rest, ct);
                return rest.ToArray();
            }

            using var all = new MemoryStream();
            await stream.CopyToAsync(all, ct);
            return all.ToArray();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<(IReadOnlyList<TextChunk> Chunks, int? PageCount, bool ArabicReordered)> ExtractAndChunkAsync(DetectedFileType type, byte[] content, CancellationToken ct)
    {
        var o = options.Value;
        var timeout = TimeSpan.FromSeconds(o.ParseTimeoutSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // Parsers are synchronous and CPU-bound: run them off the worker thread and bound the wait (SEC-5).
        var work = Task.Run(() =>
        {
            var extracted = DocumentTextExtractor.Extract(type, content, o, cts.Token);
            return (chunker.Chunk(extracted.Segments, cts.Token), extracted.PageCount, extracted.ArabicReordered);
        }, CancellationToken.None);

        try
        {
            return await work.WaitAsync(timeout + TimeoutGrace, ct);
        }
        catch (Exception ex) when ((ex is OperationCanceledException && !ct.IsCancellationRequested) || ex is TimeoutException)
        {
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // observe a late failure
            throw new DocumentParseException($"Processing the document took longer than {o.ParseTimeoutSeconds} seconds and was stopped.");
        }
    }

    private async Task<bool> StoreResultAsync(
        ClaimedJob job, Guid userId, int totalLimit, IReadOnlyList<TextChunk> chunks, GeneratedEmbeddings<Embedding<float>> embeddings, int? pageCount, string? extractionNotice, CancellationToken ct)
    {
        var documentId = job.DocumentId;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            if (totalLimit > 0)
            {
                // Same per-user advisory lock as the upload: documents of one user finish one after the other, so two of them
                // cannot both pass the check and together exceed the total. Ready documents are only ever marked Ready below, under this lock.
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 0))", ct);
                var used = await UsedChunksAsync(userId, documentId, ct);
                if (used + chunks.Count > totalLimit)
                {
                    throw new DocumentParseException(StorageLimitMessage(used, totalLimit, chunks.Count)); // rolls back; the caller fails the document
                }
            }

            // A retried job may have left chunks behind; replace them.
            await db.Chunks.IgnoreQueryFilters().Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);
            for (var i = 0; i < chunks.Count; i++)
            {
                db.Chunks.Add(new Chunk
                {
                    Id = Guid.NewGuid(),
                    DocumentId = documentId,
                    UserId = userId, // the document owner (tenant column)
                    Ordinal = chunks[i].Ordinal,
                    Text = chunks[i].Text,
                    PageNumber = chunks[i].PageNumber,
                    Section = chunks[i].Section,
                    TokenCount = chunks[i].TokenCount,
                    EmbeddingModel = EmbeddingOptions.ModelName,
                    Embedding = new Vector(embeddings[i].Vector.ToArray()),
                });
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            var updated = await db.Documents.IgnoreQueryFilters().Where(d => d.Id == documentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DocumentStatus.Ready)
                    .SetProperty(d => d.FailureReason, (string?)null)
                    .SetProperty(d => d.PageCount, pageCount)
                    .SetProperty(d => d.ExtractionNotice, extractionNotice)
                    .SetProperty(d => d.ChunkCount, chunks.Count), ct);
            if (updated == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            await db.IngestionJobs.IgnoreQueryFilters().Where(j => j.Id == job.JobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, IngestionJobState.Succeeded)
                    .SetProperty(j => j.LastError, (string?)null)
                    .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
            await db.DocumentUploads.Where(u => u.DocumentId == documentId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
    }

    /// <summary>The user's total chunk cap: BYOK when a key is saved, else demo (the same rule as the upload limits).</summary>
    private async Task<int> TotalChunkLimitAsync(Guid userId, CancellationToken ct)
    {
        var isByok = await db.UserLlmCredentials.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId, ct);
        return (isByok ? limitOptions.Value.Byok : limitOptions.Value.Demo).MaxTotalChunks;
    }

    private Task<int> UsedChunksAsync(Guid userId, Guid exceptDocumentId, CancellationToken ct) =>
        db.Documents.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.UserId == userId && d.Id != exceptDocumentId && d.Status == DocumentStatus.Ready)
            .SumAsync(d => d.ChunkCount, ct);

    private static string StorageLimitMessage(int used, int limit, int needed) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Storage limit reached: {used:N0} of {limit:N0} chunks used, and this document needs {needed:N0} more. Delete a document to free space.");

    private async Task FailAsync(ClaimedJob job, string reason, CancellationToken ct, string? lastError = null)
    {
        var documentId = job.DocumentId;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Documents.IgnoreQueryFilters().Where(d => d.Id == documentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DocumentStatus.Failed)
                    .SetProperty(d => d.FailureReason, reason), ct);
            await db.IngestionJobs.IgnoreQueryFilters().Where(j => j.Id == job.JobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, IngestionJobState.Failed)
                    .SetProperty(j => j.LastError, lastError ?? reason)
                    .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
            await db.DocumentUploads.Where(u => u.DocumentId == documentId).ExecuteDeleteAsync(ct); // D15: no bytes kept
            await tx.CommitAsync(ct);
        });
    }

    private async Task ReleaseAsync(ClaimedJob job)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await db.IngestionJobs.IgnoreQueryFilters().Where(j => j.Id == job.JobId && j.State == IngestionJobState.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, IngestionJobState.Queued)
                    .SetProperty(j => j.Attempts, j => j.Attempts > 0 ? j.Attempts - 1 : 0)
                    .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), limit.Token);
            await db.Documents.IgnoreQueryFilters().Where(d => d.Id == job.DocumentId && d.Status == DocumentStatus.Processing)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DocumentStatus.Pending), limit.Token);
            logger.LogWarning("Ingestion job {JobId} was interrupted by shutdown and put back in the queue.", job.JobId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ingestion job {JobId} was interrupted by shutdown; it stays Running and is recovered at the next start.", job.JobId);
        }
    }

    private async Task RequeueAsync(ClaimedJob job, string error, CancellationToken ct)
    {
        var documentId = job.DocumentId;
        await db.IngestionJobs.IgnoreQueryFilters().Where(j => j.Id == job.JobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, IngestionJobState.Queued)
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);
        await db.Documents.IgnoreQueryFilters().Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DocumentStatus.Pending), ct);
    }
}
