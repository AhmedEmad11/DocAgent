using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocAgent.Infrastructure.Ingestion;
using DocAgent.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocAgent.IntegrationTests;

/// <summary>
/// The total chunk cap per user (SRS DOC-3), enforced by the ingestion worker with the real ONNX model. The cap is a small number here:
/// the tests measure how many chunks one sample document makes and set the cap relative to that (the option objects are singletons).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChunkLimitTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly byte[] Sample = Encoding.UTF8.GetBytes(TestFiles.Prose(900));

    private readonly List<DocAgentApiFactory> _factories = [];
    private string _cs = null!;

    public async Task InitializeAsync()
    {
        _ = ModelLocator.Directory;
        _cs = await TestDatabase.CreateAsync(postgres);
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }
    }

    private DocAgentApiFactory NewFactory()
    {
        var factory = new DocAgentApiFactory(_cs, new Dictionary<string, string?> { ["Documents:Ingestion:PollIntervalSeconds"] = "1" });
        _factories.Add(factory);
        return factory;
    }

    private static void SetCaps(DocAgentApiFactory factory, int demo, int byok)
    {
        var limits = factory.Services.GetRequiredService<IOptions<DocumentLimitsOptions>>().Value;
        limits.Demo.MaxTotalChunks = demo;
        limits.Byok.MaxTotalChunks = byok;
        limits.Byok.MaxDocuments = 100;
    }

    private static async Task<int> ChunkCountAsync(JsonElement doc) => await Task.FromResult(doc.GetProperty("chunkCount").GetInt32());

    private static async Task<JsonElement> ChunksOfMeAsync(TestUser user) =>
        (await user.Client.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("chunks");

    /// <summary>Uploads the sample once with no cap in the way, returns how many chunks it makes, and deletes it again.</summary>
    private static async Task<int> MeasureAsync(TestUser user)
    {
        var id = await user.UploadOkAsync(Sample, "measure.txt");
        var doc = await user.WaitForFinalStatusAsync(id);
        Assert.Equal("Ready", doc.GetProperty("status").GetString());
        await user.Client.DeleteAsync($"/api/v1/documents/{id}");
        return await ChunkCountAsync(doc);
    }

    [Fact]
    public async Task A_document_that_would_pass_the_total_fails_cleanly_and_deleting_frees_the_capacity()
    {
        var factory = NewFactory();
        var user = await DocumentTestHelpers.CreateUserAsync(factory);
        var other = await DocumentTestHelpers.CreateUserAsync(factory);
        var c = await MeasureAsync(user);
        Assert.True(c >= 2, "the sample should make several chunks");
        SetCaps(factory, demo: c + 1, byok: 0);

        var first = await user.WaitForFinalStatusAsync(await user.UploadOkAsync(Sample, "one.txt"));
        var second = await user.WaitForFinalStatusAsync(await user.UploadOkAsync(Sample, "two.txt"));

        Assert.Equal("Ready", first.GetProperty("status").GetString());
        Assert.Equal("Failed", second.GetProperty("status").GetString());
        var reason = second.GetProperty("failureReason").GetString()!;
        Assert.Contains("Storage limit reached", reason);
        Assert.Contains($"{c:N0} of {c + 1:N0} chunks used", reason);
        Assert.Equal(0, second.GetProperty("chunkCount").GetInt32());
        await using (var db = DocumentTestHelpers.OpenDb(_cs))
        {
            Assert.False(await db.Chunks.IgnoreQueryFilters().AnyAsync(x => x.DocumentId == second.GetProperty("id").GetGuid()));
            Assert.False(await db.DocumentUploads.AnyAsync(u => u.DocumentId == second.GetProperty("id").GetGuid())); // bytes discarded
        }

        var usage = await ChunksOfMeAsync(user);
        Assert.Equal(c, usage.GetProperty("used").GetInt32());
        Assert.Equal(c + 1, usage.GetProperty("limit").GetInt32());

        // Another user has their own allowance (same cap, nothing used).
        var others = await other.WaitForFinalStatusAsync(await other.UploadOkAsync(Sample, "other.txt"));
        Assert.Equal("Ready", others.GetProperty("status").GetString());

        // Deleting frees capacity.
        await user.Client.DeleteAsync($"/api/v1/documents/{first.GetProperty("id").GetGuid()}");
        Assert.Equal(0, (await ChunksOfMeAsync(user)).GetProperty("used").GetInt32());
        var third = await user.WaitForFinalStatusAsync(await user.UploadOkAsync(Sample, "three.txt"));
        Assert.Equal("Ready", third.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_byok_cap_applies_to_users_with_a_key_and_the_demo_cap_to_the_others()
    {
        var factory = NewFactory();
        var keyed = await DocumentTestHelpers.CreateUserAsync(factory);
        var demo = await DocumentTestHelpers.CreateUserAsync(factory);
        await DocumentTestHelpers.AddCredentialAsync(_cs, keyed.Id);
        var c = await MeasureAsync(demo);
        SetCaps(factory, demo: c - 1, byok: 2 * c); // the demo user cannot hold even one sample; the keyed user holds two

        var demoDoc = await demo.WaitForFinalStatusAsync(await demo.UploadOkAsync(Sample, "d.txt"));
        var k1 = await keyed.WaitForFinalStatusAsync(await keyed.UploadOkAsync(Sample, "k1.txt"));
        var k2 = await keyed.WaitForFinalStatusAsync(await keyed.UploadOkAsync(Sample, "k2.txt"));
        var k3 = await keyed.WaitForFinalStatusAsync(await keyed.UploadOkAsync(Sample, "k3.txt"));

        Assert.Equal("Failed", demoDoc.GetProperty("status").GetString());
        Assert.Equal(["Ready", "Ready", "Failed"], new[] { k1, k2, k3 }.Select(d => d.GetProperty("status").GetString()));
        Assert.Equal(2 * c, (await ChunksOfMeAsync(keyed)).GetProperty("used").GetInt32());
        Assert.Equal(2 * c, (await ChunksOfMeAsync(keyed)).GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Two_workers_ingesting_one_users_documents_at_once_cannot_exceed_the_total()
    {
        var a = NewFactory();
        var b = NewFactory(); // a second application instance on the same database: its worker claims jobs at the same time
        var user = await DocumentTestHelpers.CreateUserAsync(a);
        await DocumentTestHelpers.AddCredentialAsync(_cs, user.Id);
        var c = await MeasureAsync(user);
        var cap = 2 * c + 1;
        SetCaps(a, demo: 0, byok: cap);
        SetCaps(b, demo: 0, byok: cap);

        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await user.UploadOkAsync(Sample, $"p{i}.txt"));
        }

        var finals = new List<JsonElement>();
        foreach (var id in ids)
        {
            finals.Add(await user.WaitForFinalStatusAsync(id));
        }

        Assert.Equal(2, finals.Count(d => d.GetProperty("status").GetString() == "Ready"));
        Assert.Equal(4, finals.Count(d => d.GetProperty("status").GetString() == "Failed" && d.GetProperty("failureReason").GetString()!.Contains("Storage limit reached")));
        await using var db = DocumentTestHelpers.OpenDb(_cs);
        Assert.True(await db.Chunks.IgnoreQueryFilters().CountAsync(x => x.UserId == user.Id) <= cap);
    }
}
