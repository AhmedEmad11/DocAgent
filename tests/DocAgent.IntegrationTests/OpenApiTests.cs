using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocAgent.IntegrationTests;

/// <summary>OpenAPI completeness (Phase 7): every operation is documented, its responses and security match how it is mapped.</summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenApiTests(PostgresFixture postgres)
{
    private static readonly string[] SuccessCodes = ["200", "201", "202", "204", "302"];

    private async Task<(JsonElement Doc, IReadOnlyDictionary<string, AuthDecision> Endpoints)> LoadAsync()
    {
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(cs, new Dictionary<string, string?> { ["Documents:Ingestion:WorkerEnabled"] = "false" });
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return (doc, EndpointInventory.Discover(factory.Services));
    }

    private static IEnumerable<(string Key, JsonElement Operation)> Operations(JsonElement doc)
    {
        foreach (var path in doc.GetProperty("paths").EnumerateObject())
        {
            foreach (var op in path.Value.EnumerateObject().Where(o => o.Name is "get" or "post" or "put" or "delete" or "patch"))
            {
                yield return ($"{op.Name.ToUpperInvariant()} {path.Name}", op.Value);
            }
        }
    }

    [Fact]
    public async Task The_document_is_served_anonymously_and_read_only_outside_development()
    {
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(cs, new Dictionary<string, string?> { ["Documents:Ingestion:WorkerEnabled"] = "false" });
        Assert.False(factory.Services.GetRequiredService<IHostEnvironment>().IsDevelopment());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/openapi/v1.json", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/scalar/v1")).StatusCode); // no interactive UI
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger")).StatusCode);
    }

    [Fact]
    public async Task Every_endpoint_is_in_the_document_with_summary_description_and_responses()
    {
        var (doc, endpoints) = await LoadAsync();
        var operations = Operations(doc).ToDictionary(o => o.Key, o => o.Operation);

        // Same set as the mapped endpoints (the provider callback is a browser redirect target, not an API call; health probes are not API operations).
        var expected = endpoints.Keys
            .Where(k => !k.Contains("/external/callback") && !k.Contains("/health/") && !k.Contains("/openapi/"))
            .Select(k => System.Text.RegularExpressions.Regex.Replace(k, @"\{(\w+)\}", "{$1}")).Order().ToList();
        Assert.Equal(expected, operations.Keys.Order().ToList());

        foreach (var (key, op) in operations)
        {
            Assert.True(op.TryGetProperty("summary", out var summary) && !string.IsNullOrWhiteSpace(summary.GetString()), $"{key}: summary");
            Assert.True(op.TryGetProperty("description", out var description) && description.GetString()!.Length >= 20, $"{key}: description");

            var responses = op.GetProperty("responses");
            Assert.True(SuccessCodes.Any(c => responses.TryGetProperty(c, out _)), $"{key}: no success response");
            Assert.True(responses.TryGetProperty("500", out _), $"{key}: 500 problem response");
            foreach (var response in responses.EnumerateObject())
            {
                Assert.True(response.Value.TryGetProperty("description", out var d) && !string.IsNullOrWhiteSpace(d.GetString()), $"{key} {response.Name}: description");
                // Identity's own error responses (a bare 404 on /manage/2fa, ...) carry no body schema; everything declared here does.
                if ((response.Name.StartsWith('4') || response.Name.StartsWith('5')) && (!key.Contains("/api/v1/auth/") || response.Name is "401" or "403" or "429" or "500"))
                {
                    Assert.True(response.Value.TryGetProperty("content", out var problemContent) && problemContent.TryGetProperty("application/problem+json", out _), $"{key} {response.Name}: problem+json");
                }
            }
        }
    }

    [Fact]
    public async Task Security_follows_the_auth_decision_of_each_endpoint()
    {
        var (doc, endpoints) = await LoadAsync();

        var scheme = doc.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());

        foreach (var (key, op) in Operations(doc))
        {
            var decision = endpoints[key];
            var responses = op.GetProperty("responses");
            if (decision == AuthDecision.Public)
            {
                Assert.False(op.TryGetProperty("security", out var none) && none.GetArrayLength() > 0, $"{key}: public endpoint must not require Bearer");
                continue;
            }

            Assert.True(op.TryGetProperty("security", out var security), $"{key}: missing security");
            Assert.Contains(security.EnumerateArray(), requirement => requirement.TryGetProperty("Bearer", out _));
            Assert.True(responses.TryGetProperty("401", out _), $"{key}: 401");
            if (decision == AuthDecision.ConfirmedEmail)
            {
                Assert.True(responses.TryGetProperty("403", out _), $"{key}: 403 for an unconfirmed email");
            }
        }
    }

    [Fact]
    public async Task Rate_limited_endpoints_document_429_and_main_requests_have_examples()
    {
        var (doc, _) = await LoadAsync();
        var operations = Operations(doc).ToDictionary(o => o.Key, o => o.Operation);

        foreach (var key in new[]
        {
            "POST /api/v1/auth/login", "POST /api/v1/auth/register", "POST /api/v1/auth/refresh", "POST /api/v1/auth/forgotPassword", "POST /api/v1/auth/resetPassword",
            "POST /api/v1/auth/external/exchange", "PUT /api/v1/me/llm-key", "GET /api/v1/providers/{provider}/models", "POST /api/v1/ask", "POST /api/v1/ask/stream",
            "POST /api/v1/documents", "POST /api/v1/documents/{id}/summary", "POST /api/v1/documents/compare",
        })
        {
            Assert.True(operations[key].GetProperty("responses").TryGetProperty("429", out _), $"{key}: 429");
        }

        foreach (var key in new[]
        {
            "POST /api/v1/auth/login", "POST /api/v1/auth/register", "PUT /api/v1/me/llm-key", "POST /api/v1/ask", "POST /api/v1/ask/stream",
            "POST /api/v1/documents/{id}/summary", "POST /api/v1/documents/compare", "POST /api/v1/auth/external/exchange",
        })
        {
            Assert.True(operations[key].TryGetProperty("requestBody", out var requestBody), $"{key}: request body");
            var content = requestBody.GetProperty("content");
            Assert.Contains(content.EnumerateObject(), media => media.Value.TryGetProperty("example", out _));
        }

        var upload = operations["POST /api/v1/documents"].GetProperty("requestBody").GetProperty("content");
        Assert.True(upload.TryGetProperty("multipart/form-data", out _));
        foreach (var code in new[] { "202", "400", "409", "413", "415" })
        {
            Assert.True(operations["POST /api/v1/documents"].GetProperty("responses").TryGetProperty(code, out _), $"upload {code}");
        }

        Assert.True(operations["POST /api/v1/ask/stream"].GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("text/event-stream", out _));
    }
}
