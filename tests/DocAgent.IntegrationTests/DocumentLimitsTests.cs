using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.IntegrationTests;

/// <summary>Upload limits (D19, DOC-3) and upload behaviour that must not depend on processing, so the worker is switched off.</summary>
[Collection(PostgresCollection.Name)]
public sealed class DocumentLimitsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const long MB = 1024 * 1024;
    private DocAgentApiFactory _factory = null!;
    private string _cs = null!;

    public async Task InitializeAsync()
    {
        _cs = await TestDatabase.CreateAsync(postgres);
        _factory = new DocAgentApiFactory(_cs, new Dictionary<string, string?> { ["Documents:Ingestion:WorkerEnabled"] = "false" });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static byte[] B(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static byte[] TextOfSize(long bytes) => Enumerable.Repeat((byte)'a', (int)bytes).ToArray();

    private static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;

    [Fact]
    public async Task Upload_only_stages_the_bytes_and_queues_a_job()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        var id = await user.UploadOkAsync("hello staged world"u8.ToArray(), "a.txt");

        await using var db = DocumentTestHelpers.OpenDb(_cs);
        var doc = await db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == id);
        Assert.Equal(DocumentStatus.Pending, doc.Status);
        Assert.Equal(user.Id, doc.UserId);
        Assert.Equal("hello staged world"u8.ToArray(), (await db.DocumentUploads.SingleAsync(u => u.DocumentId == id)).Content);
        var job = await db.IngestionJobs.IgnoreQueryFilters().SingleAsync(j => j.DocumentId == id);
        Assert.Equal(IngestionJobState.Queued, job.State);
        Assert.Equal(0, job.Attempts);
        Assert.Equal(user.Id, job.UserId);
    }

    [Fact]
    public async Task Demo_user_is_limited_to_three_documents_with_a_hint_to_add_a_key()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        for (var i = 0; i < 3; i++)
        {
            await user.UploadOkAsync(B($"document {i}"), $"d{i}.txt");
        }

        var fourth = await user.UploadAsync("document 4"u8.ToArray(), "d4.txt");

        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
        var detail = await DetailAsync(fourth);
        Assert.Contains("3 documents", detail);
        Assert.Contains("API key", detail);
        Assert.Equal(3, (await user.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents"))!.Length);

        // Deleting one makes room again.
        var first = (await user.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents"))![0].GetProperty("id").GetGuid();
        await user.Client.DeleteAsync($"/api/v1/documents/{first}");
        await user.UploadOkAsync("document 5"u8.ToArray(), "d5.txt");
    }

    [Fact]
    public async Task Parallel_uploads_cannot_exceed_the_document_limit()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => user.UploadAsync(B($"parallel {i}"), $"p{i}.txt")));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        await using var db = DocumentTestHelpers.OpenDb(_cs);
        Assert.Equal(3, await db.Documents.IgnoreQueryFilters().CountAsync(d => d.UserId == user.Id));
    }

    [Fact]
    public async Task Demo_user_file_size_limit_is_5_mb()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);

        var atLimit = await user.UploadAsync(TextOfSize(5 * MB), "exactly5.txt");
        Assert.Equal(HttpStatusCode.Accepted, atLimit.StatusCode);

        var tooBig = await user.UploadAsync(TextOfSize(5 * MB + 1), "over5.txt");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        var detail = await DetailAsync(tooBig);
        Assert.Contains("5 MB", detail);
        Assert.Contains("API key", detail);
        Assert.Equal("application/problem+json", tooBig.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Byok_user_has_bigger_limits()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        await DocumentTestHelpers.AddCredentialAsync(_cs, user.Id);

        // 6 MB would be refused for a demo user.
        await user.UploadOkAsync(TextOfSize(6 * MB), "six.txt");
        // 4 documents: more than the demo limit.
        for (var i = 0; i < 3; i++)
        {
            await user.UploadOkAsync(B($"document {i}"), $"d{i}.txt");
        }

        var tooBig = await user.UploadAsync(TextOfSize(25 * MB + 1), "over25.txt");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        var detail = await DetailAsync(tooBig);
        Assert.Contains("25 MB", detail);
        Assert.DoesNotContain("API key", detail);
    }

    [Fact]
    public async Task Upload_of_25_mb_returns_within_two_seconds_without_parsing()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        await DocumentTestHelpers.AddCredentialAsync(_cs, user.Id);
        var bytes = TextOfSize(25 * MB);

        var stopwatch = Stopwatch.StartNew();
        var response = await user.UploadAsync(bytes, "big.txt");
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"upload took {stopwatch.Elapsed.TotalSeconds:F2} s");
    }

    [Fact]
    public async Task Byok_document_limit_is_configurable_and_uses_the_byok_message()
    {
        await using var factory = new DocAgentApiFactory(_cs, new Dictionary<string, string?>
        {
            ["Documents:Ingestion:WorkerEnabled"] = "false",
            ["Documents:Limits:Byok:MaxDocuments"] = "5",
        });
        var user = await DocumentTestHelpers.CreateUserAsync(factory);
        await DocumentTestHelpers.AddCredentialAsync(_cs, user.Id);
        for (var i = 0; i < 5; i++)
        {
            await user.UploadOkAsync(B($"document {i}"), $"d{i}.txt");
        }

        var sixth = await user.UploadAsync("one more"u8.ToArray(), "d6.txt");

        Assert.Equal(HttpStatusCode.Conflict, sixth.StatusCode);
        var detail = await DetailAsync(sixth);
        Assert.Contains("5 documents", detail);
        Assert.DoesNotContain("API key", detail);
    }

    [Fact]
    public async Task Removing_the_key_keeps_existing_documents_but_blocks_new_uploads_above_the_demo_limit()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        await DocumentTestHelpers.AddCredentialAsync(_cs, user.Id);
        for (var i = 0; i < 5; i++)
        {
            await user.UploadOkAsync(B($"document {i}"), $"d{i}.txt");
        }

        await DocumentTestHelpers.RemoveCredentialAsync(_cs, user.Id);

        Assert.Equal(5, (await user.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents"))!.Length); // all stay
        Assert.Equal(HttpStatusCode.Conflict, (await user.UploadAsync("new"u8.ToArray(), "new.txt")).StatusCode);

        // Down to 2 documents (under the demo limit of 3): uploading works again.
        var ids = (await user.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/documents"))!.Select(d => d.GetProperty("id").GetGuid()).ToList();
        foreach (var id in ids.Take(3))
        {
            await user.Client.DeleteAsync($"/api/v1/documents/{id}");
        }

        await user.UploadOkAsync("new"u8.ToArray(), "new.txt");
    }

    [Fact]
    public async Task Limits_are_counted_per_user()
    {
        var a = await DocumentTestHelpers.CreateUserAsync(_factory);
        var b = await DocumentTestHelpers.CreateUserAsync(_factory);
        for (var i = 0; i < 3; i++)
        {
            await a.UploadOkAsync(B($"a{i}"), $"a{i}.txt");
        }

        Assert.Equal(HttpStatusCode.Conflict, (await a.UploadAsync("a"u8.ToArray(), "a4.txt")).StatusCode);
        await b.UploadOkAsync("b"u8.ToArray(), "b.txt");
    }
}
