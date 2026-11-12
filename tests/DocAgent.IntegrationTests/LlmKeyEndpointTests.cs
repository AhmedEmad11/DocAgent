using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Llm;
using DocAgent.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.IntegrationTests;

/// <summary>/api/v1/me/llm-key and /api/v1/providers/{provider}/models against mocked provider HTTP (SRS KEY-1..5, KEY-7, SEC-1, SEC-2).</summary>
[Collection(PostgresCollection.Name)]
public sealed class LlmKeyEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string KeyA = "sk-or-v1-USERAKEY-3f9d8a7b6c5d4e3f2a1b0c9d8e7f6a5b";
    private const string KeyB = "sk-ant-api03-USERBKEY-0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    private readonly ProviderBackend _backend = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ActivityCapture _activities = new();
    private string _connectionString = null!;
    private DocAgentApiFactory _factory = null!;
    private TestUser _a = null!;
    private TestUser _b = null!;

    public async Task InitializeAsync()
    {
        _connectionString = await TestDatabase.CreateAsync(postgres);
        _factory = new DocAgentApiFactory(_connectionString, SecretAssert.VerboseLogging, configureServices: _backend.Register(_logs));
        _a = await DocumentTestHelpers.CreateUserAsync(_factory);
        _b = await DocumentTestHelpers.CreateUserAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _a.Client.Dispose();
        _b.Client.Dispose();
        _activities.Dispose();
        await _factory.DisposeAsync();
    }

    private static Task<HttpResponseMessage> Put(TestUser user, object body) => user.Client.PutAsJsonAsync("/api/v1/me/llm-key", body);

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> Models(TestUser user, string provider, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/providers/{provider}/models");
        if (key is not null)
        {
            request.Headers.Add("X-Llm-Api-Key", key);
        }

        return user.Client.SendAsync(request);
    }

    [Fact]
    public async Task Put_validates_with_one_provider_call_saves_the_key_encrypted_and_returns_only_the_last_four_characters()
    {
        var response = await Put(_a, new { provider = "OpenRouter", model = "openai/gpt-4o-mini", apiKey = KeyA });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("openrouter", body.GetProperty("provider").GetString());
        Assert.Equal("openai/gpt-4o-mini", body.GetProperty("model").GetString());
        Assert.Equal(KeyA[^4..], body.GetProperty("last4").GetString());
        Assert.True(body.TryGetProperty("updatedAt", out _));
        Assert.False(body.TryGetProperty("apiKey", out _));

        // exactly one low-cost call, to the fixed host, with the key as bearer token and the chosen model
        var call = Assert.Single(_backend.Requests);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", call.Url.ToString());
        Assert.Equal("Bearer " + KeyA, call.Headers["authorization"]);
        Assert.Contains("openai/gpt-4o-mini", call.Body);

        // stored encrypted and bound to the user
        await using var db = TestDatabase.CreateContext(_connectionString);
        var row = await db.UserLlmCredentials.IgnoreQueryFilters().SingleAsync(c => c.UserId == _a.Id);
        Assert.Equal(LlmProvider.OpenRouter, row.Provider);
        Assert.DoesNotContain("USERAKEY", row.EncryptedKey);
        Assert.NotEqual(KeyA, row.EncryptedKey);
        Assert.Equal(KeyA[^4..], row.KeyLast4);
        Assert.Equal(KeyA, _factory.Services.GetRequiredService<ILlmKeyProtector>().Unprotect(_a.Id, row.EncryptedKey));
        Assert.Null(_factory.Services.GetRequiredService<ILlmKeyProtector>().Unprotect(_b.Id, row.EncryptedKey));
        // the data protection purpose is dedicated to keys: the plain provider cannot read it
        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => _factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("other-purpose").Unprotect(row.EncryptedKey));

        await SecretAssert.NotLeakedAsync(KeyA, response, _logs, _activities);
        Assert.True((await Json(await _a.Client.GetAsync("/api/v1/me"))).GetProperty("hasLlmKey").GetBoolean());
    }

    [Fact]
    public async Task Get_returns_the_saved_configuration_and_404_when_there_is_none_and_delete_is_idempotent()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _a.Client.GetAsync("/api/v1/me/llm-key")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Put(_a, new { provider = "gemini", model = "gemini-2.5-flash", apiKey = KeyA })).StatusCode);
        var get = await _a.Client.GetAsync("/api/v1/me/llm-key");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await Json(get);
        Assert.Equal("gemini", body.GetProperty("provider").GetString());
        Assert.Equal("gemini-2.5-flash", body.GetProperty("model").GetString());
        Assert.Equal(KeyA[^4..], body.GetProperty("last4").GetString());
        Assert.DoesNotContain("USERAKEY", await get.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await _a.Client.DeleteAsync("/api/v1/me/llm-key")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _a.Client.GetAsync("/api/v1/me/llm-key")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _a.Client.DeleteAsync("/api/v1/me/llm-key")).StatusCode);
        Assert.False((await Json(await _a.Client.GetAsync("/api/v1/me"))).GetProperty("hasLlmKey").GetBoolean());
    }

    [Fact]
    public async Task Saving_again_replaces_provider_model_and_key_and_keeps_the_creation_time()
    {
        var first = await Json(await Put(_a, new { provider = "openai", model = "gpt-4o", apiKey = KeyA }));
        await Task.Delay(20);
        var second = await Json(await Put(_a, new { provider = "anthropic", model = "claude-sonnet-4-5", apiKey = KeyB }));

        Assert.Equal("anthropic", second.GetProperty("provider").GetString());
        Assert.Equal(KeyB[^4..], second.GetProperty("last4").GetString());
        Assert.True(second.GetProperty("updatedAt").GetDateTimeOffset() > first.GetProperty("updatedAt").GetDateTimeOffset());

        await using var db = TestDatabase.CreateContext(_connectionString);
        var row = await db.UserLlmCredentials.IgnoreQueryFilters().SingleAsync(c => c.UserId == _a.Id); // still one row per user
        Assert.Equal(LlmProvider.Anthropic, row.Provider);
        Assert.Equal(KeyB, _factory.Services.GetRequiredService<ILlmKeyProtector>().Unprotect(_a.Id, row.EncryptedKey));
        Assert.True(row.CreatedAt < row.UpdatedAt);
    }

    [Theory]
    [InlineData("nope", "gpt-4o", "sk-valid-looking-key-123")]
    [InlineData("0", "gpt-4o", "sk-valid-looking-key-123")]
    [InlineData("ollama", "gpt-4o", "sk-valid-looking-key-123")]
    [InlineData(null, "gpt-4o", "sk-valid-looking-key-123")]
    [InlineData("openai", null, "sk-valid-looking-key-123")]
    [InlineData("openai", "", "sk-valid-looking-key-123")]
    [InlineData("openai", "gpt 4o", "sk-valid-looking-key-123")]
    [InlineData("openai", "gpt\n4o", "sk-valid-looking-key-123")]
    [InlineData("openai", "../../etc/passwd?x=1 y", "sk-valid-looking-key-123")]
    [InlineData("openai", "gpt-4o", null)]
    [InlineData("openai", "gpt-4o", "short")]
    [InlineData("openai", "gpt-4o", "has a space in it")]
    [InlineData("openai", "gpt-4o", "line\nbreak-in-the-key")]
    public async Task Invalid_input_is_a_400_and_the_provider_is_never_called(string? provider, string? model, string? apiKey)
    {
        var response = await Put(_a, new { provider, model, apiKey });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_backend.Requests);
        Assert.Equal(HttpStatusCode.NotFound, (await _a.Client.GetAsync("/api/v1/me/llm-key")).StatusCode);
    }

    [Fact]
    public async Task Overlong_model_and_key_are_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(_a, new { provider = "openai", model = new string('m', 201), apiKey = KeyA })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(_a, new { provider = "openai", model = "gpt-4o", apiKey = new string('k', 513) })).StatusCode);
        Assert.Empty(_backend.Requests);
    }

    public static IEnumerable<object?[]> ProviderFailures() =>
    [
        [HttpStatusCode.Unauthorized, null, 400, "llm_key_invalid", null],
        [HttpStatusCode.Forbidden, null, 400, "llm_key_invalid", null],
        [HttpStatusCode.PaymentRequired, null, 402, "llm_no_credit", null],
        [HttpStatusCode.TooManyRequests, """{"error":{"type":"insufficient_quota"}}""", 402, "llm_no_credit", null],
        [HttpStatusCode.TooManyRequests, null, 429, "llm_rate_limited", "12"],
        [HttpStatusCode.NotFound, null, 400, "llm_model_unknown", null],
        [HttpStatusCode.InternalServerError, null, 503, "llm_unavailable", null],
        [HttpStatusCode.ServiceUnavailable, null, 503, "llm_unavailable", null],
        [(HttpStatusCode)529, null, 503, "llm_unavailable", null],
        [HttpStatusCode.UnprocessableEntity, null, 502, "llm_request_rejected", null],
    ];

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task Provider_failures_while_saving_become_specific_problem_details_and_nothing_is_saved(
        HttpStatusCode providerStatus, string? providerBody, int expectedStatus, string expectedCode, string? expectedRetryAfter)
    {
        // The hostile provider body echoes the key; it must not reach the client or the logs.
        var echo = providerBody ?? "{\"error\":{\"message\":\"Incorrect API key provided: " + KeyA + "\"}}";
        _backend.Chat = _ => ProviderBackend.Error(providerStatus, echo, expectedRetryAfter is null ? null : TimeSpan.FromSeconds(int.Parse(expectedRetryAfter)));

        var response = await Put(_a, new { provider = "openai", model = "gpt-4o", apiKey = KeyA });

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await Json(response);
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
        Assert.Equal(expectedRetryAfter, response.Headers.TryGetValues("Retry-After", out var v) ? v.Single() : null);
        Assert.Equal(HttpStatusCode.NotFound, (await _a.Client.GetAsync("/api/v1/me/llm-key")).StatusCode); // KEY-2: not saved
        await SecretAssert.NotLeakedAsync(KeyA, response, _logs, _activities);
    }

    [Fact]
    public async Task Models_are_listed_with_a_key_in_the_header_before_anything_is_saved_and_filtered_to_tool_capable_ones()
    {
        var response = await Models(_a, "OpenRouter", KeyA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("openrouter", body.GetProperty("provider").GetString());
        var model = Assert.Single(body.GetProperty("models").EnumerateArray().ToList());
        Assert.Equal("openai/gpt-4o-mini", model.GetProperty("id").GetString());
        var call = Assert.Single(_backend.Requests);
        Assert.Equal("Bearer " + KeyA, call.Headers["authorization"]);
        await SecretAssert.NotLeakedAsync(KeyA, response, _logs, _activities);
        Assert.Equal(HttpStatusCode.NotFound, (await _a.Client.GetAsync("/api/v1/me/llm-key")).StatusCode); // listing does not save
    }

    [Theory]
    [InlineData("openrouter", "openai/gpt-4o-mini")]
    [InlineData("anthropic", "claude-sonnet-4-5")]
    [InlineData("openai", "gpt-4o")]
    [InlineData("gemini", "gemini-2.5-flash")]
    public async Task Models_works_for_each_provider_with_the_saved_key(string provider, string expectedId)
    {
        Assert.Equal(HttpStatusCode.OK, (await Put(_a, new { provider, model = expectedId, apiKey = KeyA })).StatusCode);
        _backend.Reset();

        var response = await Models(_a, provider);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await Json(response)).GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.Equal([expectedId], ids);
        var call = Assert.Single(_backend.Requests);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Contains(KeyA, string.Join(' ', call.Headers.Values)); // key sent as header ...
        Assert.DoesNotContain(KeyA, call.Url.ToString()); // ... never in the URL
        await SecretAssert.NotLeakedAsync(KeyA, response, _logs, _activities);
    }

    [Fact]
    public async Task Models_without_header_needs_a_saved_key_for_the_same_provider()
    {
        var none = await Models(_a, "openai");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("llm_key_required", (await Json(none)).GetProperty("code").GetString());

        await Put(_a, new { provider = "anthropic", model = "claude-sonnet-4-5", apiKey = KeyA });
        _backend.Reset();
        Assert.Equal(HttpStatusCode.BadRequest, (await Models(_a, "openai")).StatusCode); // saved key belongs to another provider
        Assert.Empty(_backend.Requests); // the saved key is not sent to a provider it does not belong to
    }

    [Fact]
    public async Task Models_rejects_unknown_providers_bad_header_keys_and_maps_provider_errors()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Models(_a, "ollama", KeyA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Models(_a, "https%3A%2F%2Fevil.example", KeyA)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Models(_a, "openai", "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Models(_a, "openai", "has space in key")).StatusCode);
        Assert.Empty(_backend.Requests);

        _backend.Models = _ => ProviderBackend.Error(HttpStatusCode.Unauthorized, "{\"error\":\"bad " + KeyA + "\"}");
        var rejected = await Models(_a, "openai", KeyA);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("llm_key_invalid", (await Json(rejected)).GetProperty("code").GetString());
        await SecretAssert.NotLeakedAsync(KeyA, rejected, _logs, _activities);
    }

    [Fact]
    public async Task One_users_key_is_invisible_and_unusable_for_another_user()
    {
        await Put(_a, new { provider = "openrouter", model = "openai/gpt-4o-mini", apiKey = KeyA });
        _backend.Reset();

        Assert.Equal(HttpStatusCode.NotFound, (await _b.Client.GetAsync("/api/v1/me/llm-key")).StatusCode);
        Assert.False((await Json(await _b.Client.GetAsync("/api/v1/me"))).GetProperty("hasLlmKey").GetBoolean());
        var models = await Models(_b, "openrouter"); // B has no key: A's key is not used
        Assert.Equal(HttpStatusCode.BadRequest, models.StatusCode);
        Assert.Empty(_backend.Requests);

        Assert.Equal(HttpStatusCode.NoContent, (await _b.Client.DeleteAsync("/api/v1/me/llm-key")).StatusCode); // B's delete touches nothing of A
        var still = await _a.Client.GetAsync("/api/v1/me/llm-key");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        // B saves their own; A's stays unchanged
        await Put(_b, new { provider = "anthropic", model = "claude-sonnet-4-5", apiKey = KeyB });
        Assert.Equal("openrouter", (await Json(await _a.Client.GetAsync("/api/v1/me/llm-key"))).GetProperty("provider").GetString());
        Assert.Equal(KeyB[^4..], (await Json(await _b.Client.GetAsync("/api/v1/me/llm-key"))).GetProperty("last4").GetString());
    }

    [Fact]
    public async Task Anonymous_and_unconfirmed_users_cannot_use_the_key_endpoints()
    {
        using var anonymous = _factory.CreateClient();
        var unconfirmed = await DocumentTestHelpers.CreateUserAsync(_factory, confirmEmail: false);
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, "/api/v1/me/llm-key"), (HttpMethod.Put, "/api/v1/me/llm-key"),
                     (HttpMethod.Delete, "/api/v1/me/llm-key"), (HttpMethod.Get, "/api/v1/providers/openai/models"),
                 })
        {
            using var r1 = new HttpRequestMessage(method, url);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(r1)).StatusCode);
            using var r2 = new HttpRequestMessage(method, url);
            Assert.Equal(HttpStatusCode.Forbidden, (await unconfirmed.Client.SendAsync(r2)).StatusCode);
        }

        Assert.Empty(_backend.Requests);
        unconfirmed.Client.Dispose();
    }

    [Fact]
    public async Task Deleting_the_account_removes_the_saved_key()
    {
        var user = await DocumentTestHelpers.CreateUserAsync(_factory);
        await Put(user, new { provider = "openai", model = "gpt-4o", apiKey = KeyA });
        await using (var db = TestDatabase.CreateContext(_connectionString))
        {
            Assert.True(await db.UserLlmCredentials.IgnoreQueryFilters().AnyAsync(c => c.UserId == user.Id));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync("/api/v1/me")).StatusCode);

        await using var check = TestDatabase.CreateContext(_connectionString);
        Assert.False(await check.UserLlmCredentials.IgnoreQueryFilters().AnyAsync(c => c.UserId == user.Id));
        user.Client.Dispose();
    }

    [Fact]
    public async Task The_key_never_appears_in_responses_logs_or_traces_across_save_get_models_and_failures()
    {
        var responses = new List<HttpResponseMessage>
        {
            await Put(_a, new { provider = "openai", model = "gpt-4o", apiKey = KeyA }),
            await _a.Client.GetAsync("/api/v1/me/llm-key"),
            await Models(_a, "openai"),
            await Models(_a, "openrouter", KeyB),
        };
        _backend.Chat = _ => ProviderBackend.Error(HttpStatusCode.Unauthorized, $"echo {KeyB}");
        responses.Add(await Put(_a, new { provider = "openai", model = "gpt-4o", apiKey = KeyB }));
        _backend.Chat = _ => throw new HttpRequestException($"connection to host failed for {KeyB}");
        responses.Add(await Put(_a, new { provider = "anthropic", model = "claude", apiKey = KeyB }));
        responses.Add(await _a.Client.DeleteAsync("/api/v1/me/llm-key"));

        foreach (var response in responses)
        {
            await SecretAssert.NotLeakedAsync(KeyA, response, _logs, _activities);
            await SecretAssert.NotLeakedAsync(KeyB, response, _logs, _activities);
        }

        Assert.NotEmpty(_logs.Lines); // the capture is really running
        Assert.Contains(_logs.Lines, l => l.Contains("LLM key validation failed"));
        Assert.NotEmpty(_activities.Items);
    }

    [Fact]
    public async Task Key_validation_is_rate_limited_per_user()
    {
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(
            cs, new Dictionary<string, string?> { ["RateLimiting:LlmKey:PermitLimit"] = "3" }, raiseRateLimit: false,
            configureServices: new ProviderBackend().Register());
        var user = await DocumentTestHelpers.CreateUserAsync(factory);
        var other = await DocumentTestHelpers.CreateUserAsync(factory);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Put(user, new { provider = "openai", model = "gpt-4o", apiKey = KeyA })).StatusCode);
        }

        var limited = await Put(user, new { provider = "openai", model = "gpt-4o", apiKey = KeyA });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Models(user, "openai", KeyA)).StatusCode); // shared budget
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/v1/me/llm-key")).StatusCode); // reads are not limited
        Assert.Equal(HttpStatusCode.OK, (await Put(other, new { provider = "openai", model = "gpt-4o", apiKey = KeyA })).StatusCode); // per user
        user.Client.Dispose();
        other.Client.Dispose();
    }
}
