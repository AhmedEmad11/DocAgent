using System.Text;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Retrieval;
using DocAgent.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pgvector;

namespace DocAgent.IntegrationTests;

/// <summary>
/// Shared setup for the retrieval and /ask tests (ingesting documents with the real ONNX model takes a while, so it happens once):
/// user A owns a two-page PDF and a text file; user B owns a similar price document; plus a user without documents,
/// an unconfirmed user and a document that is not Ready.
/// </summary>
public class QaScenario : IAsyncLifetime
{
    private readonly PostgresFixture postgres;

    public QaScenario(PostgresFixture postgres)
    {
        this.postgres = postgres;
        LlmFactory = new FakeLlmClientFactory(Llm);
    }

    public const string PricingQuestion = "How much does the Pro plan cost per month?";

    /// <summary>The Phase 3 tests exercise the single-pass path; agent tests use <see cref="AgentQaScenario"/>.</summary>
    public virtual string Mode => "SinglePass";

    public FakeChatClient Llm { get; } = new();
    public FakeLlmClientFactory LlmFactory { get; }
    public DocAgentApiFactory Factory { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;
    public TestUser UserA { get; private set; } = null!;
    public TestUser UserB { get; private set; } = null!;
    public TestUser EmptyUser { get; private set; } = null!;
    public TestUser UnconfirmedUser { get; private set; } = null!;
    public Guid PlansPdf { get; private set; }
    public Guid HolidaysTxt { get; private set; }
    public Guid UserBPrices { get; private set; }
    public Guid PendingDocument { get; private set; }

    public async Task InitializeAsync()
    {
        _ = ModelLocator.Directory;
        ConnectionString = await TestDatabase.CreateAsync(postgres);
        Factory = new DocAgentApiFactory(
            ConnectionString,
            new Dictionary<string, string?> { ["Documents:Ingestion:PollIntervalSeconds"] = "1", ["Qa:Mode"] = Mode },
            configureServices: FakeLlmClientFactory.Register(LlmFactory));

        UserA = await DocumentTestHelpers.CreateUserAsync(Factory);
        UserB = await DocumentTestHelpers.CreateUserAsync(Factory);
        EmptyUser = await DocumentTestHelpers.CreateUserAsync(Factory);
        UnconfirmedUser = await DocumentTestHelpers.CreateUserAsync(Factory, confirmEmail: false);

        PlansPdf = await UploadReadyAsync(UserA, "plans.pdf", TestFiles.EnglishPdf(
            "Pricing. The Pro plan costs 20 dollars per month. The Basic plan costs 10 dollars per month. Billing is monthly and invoices are sent by email.",
            "Support. Premium support responds within 4 hours on business days. The enterprise contract includes a dedicated account manager."));
        HolidaysTxt = await UploadReadyAsync(UserA, "holidays.txt", Encoding.UTF8.GetBytes(
            "Company holidays. Employees receive 25 days of paid vacation per year. The office is closed between Christmas and New Year."));
        UserBPrices = await UploadReadyAsync(UserB, "prices.txt", Encoding.UTF8.GetBytes(
            "Pricing. The Pro plan costs 99 dollars per month. The Basic plan costs 49 dollars per month. Billing is yearly."));

        // A document that matches the pricing question best but is still being processed.
        PendingDocument = await AddNonReadyDocumentAsync(UserA, "draft-prices.txt", "The Pro plan costs 20 dollars per month. Pro plan monthly price: 20 dollars.");
    }

    public async Task DisposeAsync()
    {
        foreach (var user in new[] { UserA, UserB, EmptyUser, UnconfirmedUser })
        {
            user.Client.Dispose();
        }

        await Factory.DisposeAsync();
    }

    public Retriever CreateRetriever(TestUser user, RetrievalOptions? options = null) => new(
        TestDatabase.CreateContext(ConnectionString, user.Id),
        Factory.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
        Options.Create(options ?? new RetrievalOptions()),
        NullLogger<Retriever>.Instance);

    private static async Task<Guid> UploadReadyAsync(TestUser user, string fileName, byte[] bytes)
    {
        var id = await user.UploadOkAsync(bytes, fileName);
        Assert.Equal("Ready", (await user.WaitForFinalStatusAsync(id)).GetProperty("status").GetString());
        return id;
    }

    private async Task<Guid> AddNonReadyDocumentAsync(TestUser user, string fileName, string text)
    {
        var embedder = Factory.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var embedding = await embedder.GenerateAsync([EmbeddingOptions_PassagePrefix + text]);
        await using var db = TestDatabase.CreateContext(ConnectionString, user.Id);
        var doc = new Document
        {
            Id = Guid.NewGuid(), UserId = user.Id, FileName = fileName, ContentType = "text/plain", SizeBytes = text.Length,
            Status = DocumentStatus.Processing, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(doc);
        db.Chunks.Add(new Chunk
        {
            Id = Guid.NewGuid(), DocumentId = doc.Id, UserId = user.Id, Ordinal = 0, Text = text, TokenCount = 20,
            EmbeddingModel = "test", Embedding = new Vector(embedding[0].Vector.ToArray()),
        });
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private const string EmbeddingOptions_PassagePrefix = DocAgent.Infrastructure.Embeddings.EmbeddingOptions.PassagePrefix;
}

/// <summary>Same data, but /ask runs the agent (the default mode).</summary>
public sealed class AgentQaScenario(PostgresFixture postgres) : QaScenario(postgres)
{
    public override string Mode => "Agent";
}
