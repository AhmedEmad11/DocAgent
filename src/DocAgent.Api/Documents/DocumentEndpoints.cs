using DocAgent.Api.Auth;
using DocAgent.Core.Abstractions;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Ingestion;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocAgent.Api.Documents;

public static class DocumentEndpoints
{
    /// <summary>Allowance for multipart framing on top of the file itself.</summary>
    private const long MultipartOverheadBytes = 1024 * 1024;

    public sealed record DocumentResponse(
        Guid Id, string FileName, string ContentType, long SizeBytes, int? PageCount, int ChunkCount,
        string Status, string? FailureReason, string? ExtractionNotice, DateTimeOffset CreatedAt)
    {
        public static DocumentResponse From(Document d) => new(
            d.Id, d.FileName, d.ContentType, d.SizeBytes, d.PageCount, d.ChunkCount,
            d.Status.ToString(), d.FailureReason, d.ExtractionNotice, d.CreatedAt);
    }

    public sealed record UploadResponse(Guid Id, string FileName, string Status);

    public static void MapDocuments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/documents").RequireAuthorization(AuthPolicies.EmailConfirmed);

        // The request-size limit is set per request inside UploadAsync (IHttpMaxRequestBodySizeFeature), from the caller's tier.
        group.MapPost("", UploadAsync)
            .DisableAntiforgery(); // bearer-token API: no cookie auth, so no CSRF surface

        group.MapGet("", async (AppDbContext db, CancellationToken ct) =>
        {
            // Tenant filter limits this to the caller's documents.
            var documents = await db.Documents.AsNoTracking().OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
            return Results.Ok(documents.Select(DocumentResponse.From));
        });

        group.MapGet("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
            return document is null ? ProblemResults.NotFound("Document not found.") : Results.Ok(DocumentResponse.From(document));
        });

        group.MapDelete("{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            // Filtered to the caller; another user's id deletes nothing and looks like a missing document.
            // Chunks, jobs and staged bytes go with it (database cascade).
            var deleted = await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(ct);
            return deleted == 0 ? ProblemResults.NotFound("Document not found.") : Results.NoContent();
        });
    }

    private static async Task<IResult> UploadAsync(
        HttpContext http,
        AppDbContext db,
        ICurrentUser currentUser,
        IOptions<DocumentLimitsOptions> limitOptions,
        IOptions<IngestionOptions> ingestionOptions,
        IngestionSignal signal,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("DocAgent.Api.Documents");
        var userId = currentUser.UserId;
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var request = http.Request;
        if (!request.HasFormContentType || !request.ContentType!.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemResults.UnsupportedMediaType("Send the file as multipart/form-data in a field named 'file'.");
        }

        // Which limits apply: users with a saved LLM key are BYOK, everyone else is demo (D19).
        var isByok = await db.UserLlmCredentials.AnyAsync(ct);
        var limits = isByok ? limitOptions.Value.Byok : limitOptions.Value.Demo;

        // 1. Refuse an oversized body before reading it: via the declared length, and via Kestrel's limit for chunked bodies.
        if (request.ContentLength is { } declared && declared > limits.MaxFileBytes + MultipartOverheadBytes)
        {
            return FileTooLarge(limits, isByok);
        }

        var maxBody = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBody is { IsReadOnly: false })
        {
            maxBody.MaxRequestBodySize = limits.MaxFileBytes + MultipartOverheadBytes;
        }

        // 2. Quick document-count check so over-limit users do not upload for nothing (re-checked atomically below).
        var existing = await db.Documents.CountAsync(ct);
        if (existing >= limits.MaxDocuments)
        {
            return DocumentLimitReached(limits, isByok);
        }

        IFormFile? file;
        try
        {
            var form = await request.ReadFormAsync(ct);
            file = form.Files.GetFile("file");
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return FileTooLarge(limits, isByok);
        }
        catch (InvalidDataException)
        {
            return ProblemResults.BadRequest("The multipart form is malformed.");
        }

        if (file is null || file.Length == 0)
        {
            return ProblemResults.BadRequest("Attach a non-empty file in a form field named 'file'.");
        }

        if (file.Length > limits.MaxFileBytes)
        {
            return FileTooLarge(limits, isByok);
        }

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(content, ct);
        }

        var fileName = FileNameSanitizer.Sanitize(file.FileName);
        var ingestion = ingestionOptions.Value;
        var sniff = FileTypeSniffer.Detect(content, fileName, ingestion.MaxDocxUncompressedBytes, ingestion.MaxDocxEntries);
        if (sniff.Type is not { } type)
        {
            return ProblemResults.UnsupportedMediaType(sniff.Error!);
        }

        var document = new Document
        {
            Id = Guid.NewGuid(),
            UserId = userId.Value,
            FileName = fileName,
            ContentType = type.ToContentType(),
            SizeBytes = content.Length,
            Status = DocumentStatus.Pending,
            CreatedAt = time.GetUtcNow(),
        };

        // Count + insert under a per-user advisory lock so parallel uploads cannot exceed the limit.
        var accepted = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({userId.Value.ToString()}, 0))", ct);
            if (await db.Documents.CountAsync(ct) >= limits.MaxDocuments)
            {
                accepted = false;
                return;
            }

            db.Documents.Add(document);
            db.DocumentUploads.Add(new DocumentUpload { DocumentId = document.Id, Content = content });
            db.IngestionJobs.Add(new IngestionJob
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                UserId = userId.Value,
                State = IngestionJobState.Queued,
                CreatedAt = document.CreatedAt,
                UpdatedAt = document.CreatedAt,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            accepted = true;
        });

        if (!accepted)
        {
            return DocumentLimitReached(limits, isByok);
        }

        signal.Notify();
        logger.LogInformation("Document {DocumentId} accepted for ingestion ({Type}, {SizeBytes} bytes).", document.Id, type, content.Length);
        return Results.Accepted($"/api/v1/documents/{document.Id}", new UploadResponse(document.Id, document.FileName, document.Status.ToString()));
    }

    private static IResult FileTooLarge(DocumentLimit limits, bool isByok) =>
        ProblemResults.PayloadTooLarge(
            $"The file is larger than the {FormatMegabytes(limits.MaxFileBytes)} limit for your account."
            + (isByok ? string.Empty : " Add your own LLM API key to raise the limit."));

    private static IResult DocumentLimitReached(DocumentLimit limits, bool isByok) =>
        ProblemResults.Conflict(
            $"You have reached the limit of {limits.MaxDocuments} documents for your account. Delete a document to upload another."
            + (isByok ? string.Empty : " Add your own LLM API key to raise the limit."));

    private static string FormatMegabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
