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
            .Select(d => new { d.UserId, d.ContentType })
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
            var content = await db.DocumentUploads.AsNoTracking()
                .Where(u => u.DocumentId == documentId).Select(u => u.Content).FirstOrDefaultAsync(ct)
                ?? throw new DocumentParseException("The uploaded file is no longer available. Please upload it again.");

            var type = DetectedFileTypeExtensions.FromContentType(document.ContentType);
            var (chunks, pageCount, arabicReordered) = await ExtractAndChunkAsync(type, content, ct);
            if (chunks.Count == 0)
            {
                throw new NoExtractableTextException("The document contains no text.");
            }

            var inputs = chunks.Select(c => EmbeddingOptions.PassagePrefix + c.Text).ToList();
            var embeddings = await embedder.GenerateAsync(inputs, cancellationToken: ct);

            if (!await StoreResultAsync(job, document.UserId, chunks, embeddings, pageCount, arabicReordered ? ArabicReorderedNotice : null, ct))
            {
                return IngestionOutcome.DocumentGone;
            }

            logger.LogInformation("Ingestion job {JobId}: document {DocumentId} is ready with {ChunkCount} chunks.", job.JobId, documentId, chunks.Count);
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
            throw; // shutting down: the job stays Running and is recovered at the next start (REL-3)
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
        ClaimedJob job, Guid userId, IReadOnlyList<TextChunk> chunks, GeneratedEmbeddings<Embedding<float>> embeddings, int? pageCount, string? extractionNotice, CancellationToken ct)
    {
        var documentId = job.DocumentId;
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

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
