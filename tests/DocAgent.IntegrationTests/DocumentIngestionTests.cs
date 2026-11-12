using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Embeddings;
using DocAgent.TestSupport;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DocAgent.IntegrationTests;

/// <summary>End-to-end ingestion with the real worker and the real ONNX model (run scripts/download-models.sh first).</summary>
[Collection(PostgresCollection.Name)]
public sealed class DocumentIngestionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private DocAgentApiFactory _factory = null!;
    private string _cs = null!;
    private TestUser _user = null!;

    public async Task InitializeAsync()
    {
        _ = ModelLocator.Directory; // fails with a clear message when the model is missing
        _cs = await TestDatabase.CreateAsync(postgres);
        _factory = new DocAgentApiFactory(_cs, new Dictionary<string, string?> { ["Documents:Ingestion:PollIntervalSeconds"] = "1" });
        _user = await DocumentTestHelpers.CreateUserAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _user.Client.Dispose();
        await _factory.DisposeAsync();
    }

    private const string Pricing = "The plan costs 20 dollars per month";

    public static TheoryData<string, string, string, bool, bool> Files => new()
    {
        // name, kind, expected text snippet, pdf (page numbers), has sections
        { "english.pdf", "pdf-en", Pricing, true, false },
        { "english.docx", "docx-en", Pricing, false, true },
        { "english.txt", "txt-en", Pricing, false, false },
        { "english.md", "md-en", Pricing, false, true },
        { "arabic.pdf", "pdf-ar", "تكلفة الخطة الشهرية عشرون دولارا", true, false },
        { "arabic-chromium.pdf", "pdf-ar-chromium", "عاصمة فرنسا هي باريس", true, false },
        { "arabic.docx", "docx-ar", "تكلفة الخطة الشهرية عشرون دولارا", false, true },
        { "arabic.txt", "txt-ar", "تكلفة الخطة الشهرية عشرون دولارا", false, false },
        { "arabic.md", "md-ar", "تكلفة الخطة الشهرية عشرون دولارا", false, true },
    };

    private static byte[] Build(string kind)
    {
        var englishBody = $"{Pricing}. " + TestFiles.Prose(700);
        var arabicBody = $"{TestFiles.ArabicSentence1} {TestFiles.ArabicSentence2} " + string.Join(' ', Enumerable.Repeat("الرجاء قراءة هذه الوثيقة بعناية قبل التوقيع عليها.", 40));
        return kind switch
        {
            "pdf-en" => TestFiles.EnglishPdf(englishBody, "Second page: " + TestFiles.Prose(200)),
            "docx-en" => TestFiles.Docx(["# Pricing", englishBody, "## Support", TestFiles.Prose(100)]),
            "txt-en" => Encoding.UTF8.GetBytes(englishBody),
            "md-en" => Encoding.UTF8.GetBytes($"# Pricing\n\n{englishBody}\n\n## Support\n\n{TestFiles.Prose(100)}\n"),
            "pdf-ar" => TestFiles.ArabicLogicalPdf(arabicBody),
            "pdf-ar-chromium" => TestFiles.ReadFixture("arabic-chromium.pdf"),
            "docx-ar" => TestFiles.Docx([$"# {TestFiles.ArabicPage1}", arabicBody, $"## {TestFiles.ArabicSentence2}", "نص إضافي قصير."], rtl: true),
            "txt-ar" => Encoding.UTF8.GetBytes(arabicBody),
            "md-ar" => Encoding.UTF8.GetBytes($"# {TestFiles.ArabicPage1}\n\n{arabicBody}\n\n## {TestFiles.ArabicSentence2}\n\nنص إضافي قصير.\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Upload_is_processed_into_chunks_with_embeddings_and_metadata(string fileName, string kind, string snippet, bool isPdf, bool hasSections)
    {
        var bytes = Build(kind);

        var response = await _user.UploadAsync(bytes, fileName);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = accepted.GetProperty("id").GetGuid();
        Assert.Equal(fileName, accepted.GetProperty("fileName").GetString());
        Assert.Equal("Pending", accepted.GetProperty("status").GetString());
        Assert.Equal($"/api/v1/documents/{id}", response.Headers.Location!.OriginalString);

        var doc = await _user.WaitForFinalStatusAsync(id);
        Assert.True(doc.GetProperty("status").GetString() == "Ready", doc.ToString());
        Assert.Equal(bytes.Length, doc.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("failureReason").ValueKind);
        if (isPdf)
        {
            Assert.True(doc.GetProperty("pageCount").GetInt32() >= 1);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, doc.GetProperty("pageCount").ValueKind);
        }

        await using var db = DocumentTestHelpers.OpenDb(_cs);
        var chunks = await db.Chunks.IgnoreQueryFilters().Where(c => c.DocumentId == id).OrderBy(c => c.Ordinal).ToListAsync();
        Assert.NotEmpty(chunks);
        Assert.Equal(chunks.Count, doc.GetProperty("chunkCount").GetInt32());
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
        Assert.Contains(snippet, string.Join("\n", chunks.Select(c => c.Text)));
        Assert.All(chunks, c =>
        {
            Assert.Equal(_user.Id, c.UserId);
            Assert.Equal(EmbeddingOptions.ModelName, c.EmbeddingModel);
            Assert.Equal(384, c.Embedding.ToArray().Length);
            Assert.Equal(1.0, Math.Sqrt(c.Embedding.ToArray().Sum(x => (double)x * x)), 3);
            Assert.InRange(c.TokenCount, 1, 480);
            Assert.Equal(isPdf, c.PageNumber is not null);
            if (isPdf)
            {
                Assert.InRange(c.PageNumber!.Value, 1, doc.GetProperty("pageCount").GetInt32());
            }

            if (hasSections)
            {
                Assert.False(string.IsNullOrEmpty(c.Section));
            }
            else
            {
                Assert.Null(c.Section);
            }
        });

        // D15: the uploaded bytes are gone once processing finished; the job is closed.
        Assert.False(await db.DocumentUploads.AnyAsync(u => u.DocumentId == id));
        var job = await db.IngestionJobs.IgnoreQueryFilters().SingleAsync(j => j.DocumentId == id);
        Assert.Equal(IngestionJobState.Succeeded, job.State);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task Pdf_page_numbers_follow_the_pages()
    {
        var id = await _user.UploadOkAsync(TestFiles.EnglishPdf("First page " + TestFiles.Prose(150), "Second page " + TestFiles.Prose(150)), "two.pdf");
        await _user.WaitForFinalStatusAsync(id);

        await using var db = DocumentTestHelpers.OpenDb(_cs);
        var chunks = await db.Chunks.IgnoreQueryFilters().Where(c => c.DocumentId == id).OrderBy(c => c.Ordinal).ToListAsync();
        Assert.Equal([1, 2], chunks.Select(c => c.PageNumber!.Value).Distinct());
        Assert.StartsWith("First page", chunks.First(c => c.PageNumber == 1).Text);
        Assert.StartsWith("Second page", chunks.First(c => c.PageNumber == 2).Text);
    }

    [Fact]
    public async Task Scanned_pdf_without_text_fails_with_the_documented_reason()
    {
        var id = await _user.UploadOkAsync(TestFiles.BlankPdf(), "scan.pdf");

        var doc = await _user.WaitForFinalStatusAsync(id);

        Assert.Equal("Failed", doc.GetProperty("status").GetString());
        Assert.Equal("No extractable text: scanned PDFs are not supported.", doc.GetProperty("failureReason").GetString());
        Assert.Equal(0, doc.GetProperty("chunkCount").GetInt32());

        await using var db = DocumentTestHelpers.OpenDb(_cs);
        Assert.False(await db.DocumentUploads.AnyAsync(u => u.DocumentId == id)); // bytes discarded on failure too
        var job = await db.IngestionJobs.IgnoreQueryFilters().SingleAsync(j => j.DocumentId == id);
        Assert.Equal(IngestionJobState.Failed, job.State);
        Assert.Equal(1, job.Attempts); // parse errors are not retried
    }

    [Fact]
    public async Task Corrupt_pdf_fails_without_retries()
    {
        var id = await _user.UploadOkAsync(Encoding.ASCII.GetBytes("%PDF-1.7\nthis is definitely not a real pdf"), "broken.pdf");

        var doc = await _user.WaitForFinalStatusAsync(id);

        Assert.Equal("Failed", doc.GetProperty("status").GetString());
        Assert.Contains("could not be read", doc.GetProperty("failureReason").GetString());
        await using var db = DocumentTestHelpers.OpenDb(_cs);
        Assert.Equal(1, (await db.IngestionJobs.IgnoreQueryFilters().SingleAsync(j => j.DocumentId == id)).Attempts);
    }

    [Fact]
    public async Task Whitespace_only_text_file_fails()
    {
        var id = await _user.UploadOkAsync(Encoding.UTF8.GetBytes("   \n\n  \n"), "blank.txt");
        var doc = await _user.WaitForFinalStatusAsync(id);
        Assert.Equal("Failed", doc.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("program.exe", new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 })]
    [InlineData("data.bin", new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData("image.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 })]
    [InlineData("noext", new byte[] { 0x68, 0x69 })]
    public async Task Unsupported_types_are_rejected_with_415(string fileName, byte[] bytes)
    {
        var response = await _user.UploadAsync(bytes, fileName);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(415, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.Empty(await _user.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents") ?? []);
    }

    [Fact]
    public async Task File_whose_extension_contradicts_its_content_is_rejected()
    {
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _user.UploadAsync(TestFiles.EnglishPdf("Hello world and more words"), "fake.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _user.UploadAsync(TestFiles.Docx(["Hello"]), "fake.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _user.UploadAsync("plain text"u8.ToArray(), "fake.docx")).StatusCode);
    }

    [Fact]
    public async Task Empty_file_and_missing_field_are_400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.UploadAsync([], "empty.txt")).StatusCode);

        using var wrongField = DocumentTestHelpers.FileContent("hello"u8.ToArray(), "a.txt", fieldName: "document");
        Assert.Equal(HttpStatusCode.BadRequest, (await _user.Client.PostAsync("/api/v1/documents", wrongField)).StatusCode);

        var notMultipart = await _user.Client.PostAsJsonAsync("/api/v1/documents", new { file = "x" });
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, notMultipart.StatusCode);
    }

    [Fact]
    public async Task File_names_are_sanitized()
    {
        var response = await _user.UploadAsync("hello there, this is a note"u8.ToArray(), "..\\..\\secret\\no\ttes‮.txt");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var name = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString();
        Assert.Equal("no tes.txt", name);
    }

    [Fact]
    public async Task List_is_newest_first_and_has_the_documented_shape()
    {
        var first = await _user.UploadOkAsync("first document text"u8.ToArray(), "first.txt");
        await Task.Delay(20);
        var second = await _user.UploadOkAsync("second document text"u8.ToArray(), "second.md");
        await _user.WaitForFinalStatusAsync(first);
        await _user.WaitForFinalStatusAsync(second);

        var list = await _user.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents");

        Assert.NotNull(list);
        Assert.Equal([second, first], list.Select(d => d.GetProperty("id").GetGuid()));
        var item = list[0];
        foreach (var property in new[] { "id", "fileName", "contentType", "sizeBytes", "pageCount", "chunkCount", "status", "failureReason", "createdAt" })
        {
            Assert.True(item.TryGetProperty(property, out _), $"missing '{property}'");
        }

        Assert.Equal("text/markdown", item.GetProperty("contentType").GetString());
        Assert.Equal("text/plain", list[1].GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Delete_removes_the_document_its_chunks_jobs_and_is_then_404()
    {
        var id = await _user.UploadOkAsync(Encoding.UTF8.GetBytes(TestFiles.Prose(300)), "to-delete.txt");
        await _user.WaitForFinalStatusAsync(id);

        var delete = await _user.Client.DeleteAsync($"/api/v1/documents/{id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.Client.GetAsync($"/api/v1/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.Client.DeleteAsync($"/api/v1/documents/{id}")).StatusCode);
        await using var db = DocumentTestHelpers.OpenDb(_cs);
        Assert.False(await db.Chunks.IgnoreQueryFilters().AnyAsync(c => c.DocumentId == id));
        Assert.False(await db.IngestionJobs.IgnoreQueryFilters().AnyAsync(j => j.DocumentId == id));
        Assert.False(await db.DocumentUploads.AnyAsync(u => u.DocumentId == id));
    }

    [Fact]
    public async Task Users_cannot_see_or_delete_each_others_documents()
    {
        var other = await DocumentTestHelpers.CreateUserAsync(_factory);
        var id = await _user.UploadOkAsync("private notes of user A"u8.ToArray(), "a.txt");
        await _user.WaitForFinalStatusAsync(id);

        // B: 404 (never 403), empty list.
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.DeleteAsync($"/api/v1/documents/{id}")).StatusCode);
        Assert.Empty(await other.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents") ?? []);

        // A's document is untouched and still complete.
        var doc = await _user.Client.GetFromJsonAsync<JsonElement>($"/api/v1/documents/{id}");
        Assert.Equal("Ready", doc.GetProperty("status").GetString());
        Assert.Single(await _user.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents") ?? []);
        await using var db = DocumentTestHelpers.OpenDb(_cs);
        Assert.True(await db.Chunks.IgnoreQueryFilters().AnyAsync(c => c.DocumentId == id));

        // B's own document does not count against A, and vice versa.
        var bId = await other.UploadOkAsync("notes of user B"u8.ToArray(), "b.txt");
        await other.WaitForFinalStatusAsync(bId);
        Assert.Single(await other.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents") ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await _user.Client.GetAsync($"/api/v1/documents/{bId}")).StatusCode);
        other.Client.Dispose();
    }

    [Fact]
    public async Task Unconfirmed_and_anonymous_callers_are_refused()
    {
        var unconfirmed = await DocumentTestHelpers.CreateUserAsync(_factory, confirmEmail: false);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await unconfirmed.UploadAsync("hello"u8.ToArray(), "a.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unconfirmed.Client.GetAsync("/api/v1/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unconfirmed.Client.GetAsync($"/api/v1/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unconfirmed.Client.DeleteAsync($"/api/v1/documents/{id}")).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/documents")).StatusCode);
        using var form = DocumentTestHelpers.FileContent("hello"u8.ToArray(), "a.txt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/documents", form)).StatusCode);
        unconfirmed.Client.Dispose();
    }

    [Fact]
    public async Task Search_finds_the_related_chunk_with_the_stored_embeddings()
    {
        // Sanity check that the stored vectors are usable: the pricing sentence is the nearest chunk to a pricing query.
        var text = "Giraffes are tall animals that live in Africa and eat leaves from tall trees.\n\n"
                   + "The plan costs 20 dollars per month and includes storage for one hundred documents.\n\n"
                   + "Volcanoes erupt when magma rises through cracks in the crust of the earth.";
        var id = await _user.UploadOkAsync(Encoding.UTF8.GetBytes(text), "mixed.txt");
        await _user.WaitForFinalStatusAsync(id);

        using var generator = new OnnxEmbeddingGenerator(ModelLocator.Directory, EmbeddingTokenizer.Load(ModelLocator.Directory));
        var query = (await generator.GenerateAsync([EmbeddingOptions.QueryPrefix + "how much does the plan cost?"]))[0].Vector.ToArray();

        await using var db = TestDatabase.CreateContext(_cs, _user.Id);
        var chunk = await db.Chunks.Where(c => c.DocumentId == id)
            .OrderBy(c => c.Embedding.CosineDistance(new Pgvector.Vector(query)))
            .FirstAsync();
        Assert.Contains("20 dollars", chunk.Text);
    }
}
