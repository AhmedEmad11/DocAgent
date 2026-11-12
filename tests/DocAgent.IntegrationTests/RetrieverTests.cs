using DocAgent.Infrastructure.Retrieval;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class RetrieverTests(QaScenario scenario) : IClassFixture<QaScenario>
{
    [Fact]
    public async Task Returns_only_the_callers_chunks_even_when_another_user_has_a_better_match()
    {
        var asA = await scenario.CreateRetriever(scenario.UserA).SearchAsync(QaScenario.PricingQuestion, null, 8);
        var asB = await scenario.CreateRetriever(scenario.UserB).SearchAsync(QaScenario.PricingQuestion, null, 8);

        Assert.NotEmpty(asA);
        Assert.NotEmpty(asB);
        Assert.All(asA, c => Assert.Contains(c.DocumentId, new[] { scenario.PlansPdf, scenario.HolidaysTxt }));
        Assert.All(asB, c => Assert.Equal(scenario.UserBPrices, c.DocumentId));
        Assert.Contains("20 dollars", asA[0].Text);
        Assert.Contains("99 dollars", asB[0].Text);
    }

    [Fact]
    public async Task Results_carry_file_page_ordinal_text_and_a_score_in_range()
    {
        var results = await scenario.CreateRetriever(scenario.UserA, new RetrievalOptions { Hybrid = false }).SearchAsync(QaScenario.PricingQuestion, null, 8);

        var best = results[0];
        Assert.Equal(scenario.PlansPdf, best.DocumentId);
        Assert.Equal("plans.pdf", best.FileName);
        Assert.Equal(1, best.PageNumber);
        Assert.True(best.Ordinal >= 0);
        Assert.False(string.IsNullOrWhiteSpace(best.Text));
        Assert.InRange(best.Score, 0.8, 1.0);
        Assert.Equal(results.OrderByDescending(r => r.Score).Select(r => r.ChunkId), results.Select(r => r.ChunkId)); // hybrid off here: best cosine first
    }

    [Fact]
    public async Task Document_ids_restrict_the_search()
    {
        var results = await scenario.CreateRetriever(scenario.UserA)
            .SearchAsync(QaScenario.PricingQuestion, [scenario.HolidaysTxt], 8, default);

        Assert.All(results, c => Assert.Equal(scenario.HolidaysTxt, c.DocumentId));
    }

    [Fact]
    public async Task Someone_elses_document_id_yields_nothing()
    {
        var results = await scenario.CreateRetriever(scenario.UserA)
            .SearchAsync(QaScenario.PricingQuestion, [scenario.UserBPrices], 8, default);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Documents_that_are_not_ready_are_excluded()
    {
        var all = await scenario.CreateRetriever(scenario.UserA).SearchAsync("Pro plan monthly price 20 dollars", null, 8);
        var byId = await scenario.CreateRetriever(scenario.UserA)
            .SearchAsync("Pro plan monthly price 20 dollars", [scenario.PendingDocument], 8, default);

        Assert.DoesNotContain(all, c => c.DocumentId == scenario.PendingDocument);
        Assert.Empty(byId);
    }

    [Fact]
    public async Task Chunks_below_min_score_are_dropped_and_top_k_and_token_budget_are_respected()
    {
        var strict = await scenario.CreateRetriever(scenario.UserA, new RetrievalOptions { MinScore = 0.99 })
            .SearchAsync(QaScenario.PricingQuestion, null, 8);
        var one = await scenario.CreateRetriever(scenario.UserA, new RetrievalOptions { MinScore = 0 })
            .SearchAsync(QaScenario.PricingQuestion, null, 1);
        var budget = await scenario.CreateRetriever(scenario.UserA, new RetrievalOptions { MinScore = 0, MaxContextTokens = 1 })
            .SearchAsync(QaScenario.PricingQuestion, null, 8);

        Assert.Empty(strict);
        Assert.Single(one);
        Assert.Single(budget); // the first chunk always fits; the cap stops the rest
    }

    [Fact]
    public async Task Hybrid_search_finds_an_exact_term_and_still_respects_tenancy()
    {
        var asA = await scenario.CreateRetriever(scenario.UserA, new RetrievalOptions { Hybrid = true })
            .SearchAsync("enterprise contract dedicated account manager", null, 8);
        var asB = await scenario.CreateRetriever(scenario.UserB, new RetrievalOptions { Hybrid = true })
            .SearchAsync("enterprise contract dedicated account manager", null, 8);

        Assert.Contains(asA, c => c.Text.Contains("dedicated account manager"));
        Assert.DoesNotContain(asB, c => c.Text.Contains("dedicated account manager"));
    }
}
