using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocAgent.Core.Entities;
using DocAgent.Infrastructure.Data;
using DocAgent.Infrastructure.Ingestion;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using DocAgent.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DocAgent.IntegrationTests;

/// <summary>Phase 7: the API-level checks from the security review (docs/SECURITY_REVIEW.md) that need no documents or model.</summary>
[Collection(PostgresCollection.Name)]
public sealed class HardeningTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string?> NoWorker = new() { ["Documents:Ingestion:WorkerEnabled"] = "false" };

    private async Task<DocAgentApiFactory> NewFactoryAsync(Dictionary<string, string?>? settings = null, bool raiseRateLimit = true)
    {
        var cs = await TestDatabase.CreateAsync(postgres);
        return new DocAgentApiFactory(cs, settings ?? NoWorker, raiseRateLimit);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
    }

    // ---- A01/A05 (API2, API5, API9): every endpoint has an explicit authentication decision ----

    [Fact]
    public async Task Every_mapped_endpoint_has_an_explicit_auth_decision()
    {
        await using var factory = await NewFactoryAsync();
        using var _ = factory.CreateClient(); // starts the host

        var actual = EndpointInventory.Discover(factory.Services);

        var unknown = actual.Keys.Except(EndpointInventory.Expected.Keys).Order().ToList();
        var missing = EndpointInventory.Expected.Keys.Except(actual.Keys).Order().ToList();
        var different = EndpointInventory.Expected.Where(e => actual.TryGetValue(e.Key, out var d) && d != e.Value)
            .Select(e => $"{e.Key}: expected {e.Value}, mapped as {actual[e.Key]}").ToList();

        Assert.True(unknown.Count == 0,
            "New endpoint(s) without an auth decision in EndpointInventory.Expected (decide whether each is public, signed-in or confirmed-email): " + string.Join("; ", unknown));
        Assert.True(missing.Count == 0, "Endpoint(s) in EndpointInventory.Expected that are no longer mapped: " + string.Join("; ", missing));
        Assert.True(different.Count == 0, "Endpoint(s) whose protection differs from the table: " + string.Join("; ", different));
    }

    [Fact]
    public async Task Protected_endpoints_answer_401_to_anonymous_callers_and_403_to_unconfirmed_users()
    {
        await using var factory = await NewFactoryAsync();
        using var anonymous = factory.CreateClient();
        var unconfirmed = await DocumentTestHelpers.CreateUserAsync(factory, confirmEmail: false);

        foreach (var (route, decision) in EndpointInventory.Expected.Where(e => e.Value != AuthDecision.Public))
        {
            var parts = route.Split(' ', 2);
            var url = parts[1].Replace("{id}", Guid.NewGuid().ToString()).Replace("{provider}", "openai");
            HttpContent? Body() => parts[0] is "GET" or "DELETE" ? null
                : url == "/api/v1/documents" ? DocumentTestHelpers.FileContent("hello"u8.ToArray(), "a.txt") : JsonContent.Create(new { });
            HttpRequestMessage Request() => new(new HttpMethod(parts[0]), url) { Content = Body() };

            var asAnonymous = await anonymous.SendAsync(Request());
            Assert.True(asAnonymous.StatusCode == HttpStatusCode.Unauthorized, $"{route}: anonymous got {(int)asAnonymous.StatusCode}, expected 401");
            Assert.Equal("application/problem+json", asAnonymous.Content.Headers.ContentType?.MediaType);

            if (decision == AuthDecision.ConfirmedEmail)
            {
                var asUnconfirmed = await unconfirmed.Client.SendAsync(Request());
                Assert.True(asUnconfirmed.StatusCode == HttpStatusCode.Forbidden, $"{route}: unconfirmed user got {(int)asUnconfirmed.StatusCode}, expected 403");
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await unconfirmed.Client.GetAsync("/api/v1/me")).StatusCode); // signed-in endpoints do not need a confirmed email
    }

    [Fact]
    public async Task Registration_ignores_extra_fields_so_an_account_cannot_confirm_itself_or_pick_roles()
    {
        await using var factory = await NewFactoryAsync();
        using var client = factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = AuthTestHelpers.Password, emailConfirmed = true, id = Guid.NewGuid(), isAdmin = true, roles = new[] { "Admin" },
            userName = "someone-else", normalizedEmail = "X@Y.Z",
        });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        var tokens = await client.LoginAsync(email);
        var me = await client.WithBearer(tokens.AccessToken).GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.False(me.GetProperty("emailConfirmed").GetBoolean());
        Assert.Equal(email, me.GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/documents")).StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        await using var factory = await NewFactoryAsync(); // the per-IP limit is raised here; the lockout is the second line of defence
        using var client = factory.CreateClient();
        var email = AuthTestHelpers.NewEmail();
        Assert.Equal(HttpStatusCode.OK, (await client.RegisterAsync(email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(factory.Emails.Last("confirm", email).Value)).StatusCode);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Wr0ng!pass" })).StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = AuthTestHelpers.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("LockedOut", await locked.Content.ReadAsStringAsync());
    }

    // ---- A05 (API8): headers, HSTS, CORS ----

    [Fact]
    public async Task Security_headers_are_on_success_error_and_not_found_responses()
    {
        await using var factory = await NewFactoryAsync();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/health/live", "/api/v1/me" /* 401 */, "/no/such/route" /* 404 */ })
        {
            var response = await client.GetAsync(path);
            var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

            Assert.True(headers.TryGetValue("X-Content-Type-Options", out var nosniff) && nosniff == "nosniff", path);
            Assert.Equal("DENY", headers["X-Frame-Options"]);
            Assert.Equal("no-referrer", headers["Referrer-Policy"]);
            Assert.Contains("default-src 'none'", headers["Content-Security-Policy"]);
            Assert.Contains("frame-ancestors 'none'", headers["Content-Security-Policy"]);
            Assert.Contains("no-store", headers["Cache-Control"]);
            Assert.Contains("camera=()", headers["Permissions-Policy"]);
            Assert.False(headers.ContainsKey("Server"), "the server software must not be advertised");
            Assert.False(headers.ContainsKey("X-Powered-By"));
        }
    }

    [Fact]
    public async Task Hsts_is_sent_over_https_outside_development_and_never_over_plain_http()
    {
        await using var factory = await NewFactoryAsync();
        using var https = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://docagent.example"), AllowAutoRedirect = false });
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var secure = await https.GetAsync("/health/live");
        Assert.True(secure.Headers.TryGetValues("Strict-Transport-Security", out var values));
        Assert.Contains("max-age=31536000", values!.Single());
        Assert.Contains("includeSubDomains", values!.Single());

        Assert.False((await http.GetAsync("/health/live")).Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Forwarded_proto_makes_hsts_work_behind_a_tls_terminating_proxy_when_enabled()
    {
        await using var factory = await NewFactoryAsync(new Dictionary<string, string?>(NoWorker) { ["ForwardedHeaders:Enabled"] = "true" });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://docagent.example"), AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.Contains("Strict-Transport-Security"));

        // Off by default: the header is then ignored (anyone could otherwise spoof their IP past the auth rate limit).
        await using var plain = await NewFactoryAsync();
        using var plainClient = plain.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://docagent.example"), AllowAutoRedirect = false });
        using var spoof = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        spoof.Headers.Add("X-Forwarded-Proto", "https");
        Assert.False((await plainClient.SendAsync(spoof)).Headers.Contains("Strict-Transport-Security"));
    }

    private static async Task<HttpResponseMessage> PreflightAsync(HttpClient client, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/ask");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Cors_is_off_unless_origins_are_configured()
    {
        await using var factory = await NewFactoryAsync();
        using var client = factory.CreateClient();

        var response = await PreflightAsync(client, "http://localhost:3000");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_allows_only_the_configured_origin_without_credentials()
    {
        await using var factory = await NewFactoryAsync(new Dictionary<string, string?>(NoWorker) { ["Cors:AllowedOrigins:0"] = "http://localhost:3000" });
        using var client = factory.CreateClient();

        var allowed = await PreflightAsync(client, "http://localhost:3000");
        Assert.Equal("http://localhost:3000", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Contains("authorization", string.Join(",", allowed.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);

        foreach (var other in new[] { "https://evil.example", "http://localhost:3001", "http://localhost:3000.evil.example", "null" })
        {
            Assert.False((await PreflightAsync(client, other)).Headers.Contains("Access-Control-Allow-Origin"), other);
        }

        // A simple cross-origin call gets the header only for the allowed origin; Retry-After is readable by the UI.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("Origin", "http://localhost:3000");
        var simple = await client.SendAsync(request);
        Assert.Equal("http://localhost:3000", simple.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Retry-After", string.Join(",", simple.Headers.GetValues("Access-Control-Expose-Headers")));
    }

    [Fact]
    public async Task A_wildcard_cors_origin_is_refused_at_startup()
    {
        await using var factory = await NewFactoryAsync(new Dictionary<string, string?>(NoWorker) { ["Cors:AllowedOrigins:0"] = "*" });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("explicit origins", ex.ToString());
    }

    // ---- A04 (API4): request size ----

    [Fact]
    public async Task Json_endpoints_refuse_bodies_over_the_limit_with_413_problem_details()
    {
        await using var factory = await NewFactoryAsync();
        using var client = factory.CreateClient();
        var big = new ByteArrayContent(new byte[300 * 1024]);
        big.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var login = await client.PostAsync("/api/v1/auth/login", big);
        await AssertProblemAsync(login, HttpStatusCode.RequestEntityTooLarge);

        var user = await DocumentTestHelpers.CreateUserAsync(factory);
        var big2 = new ByteArrayContent(new byte[300 * 1024]);
        big2.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        await AssertProblemAsync(await user.Client.PostAsync("/api/v1/ask", big2), HttpStatusCode.RequestEntityTooLarge);

        // Normal-sized requests are untouched.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "x@example.com", password = "Wr0ng!pass" })).StatusCode);
    }

    [Fact]
    public async Task The_upload_endpoint_keeps_its_own_larger_limit()
    {
        await using var factory = await NewFactoryAsync();
        var user = await DocumentTestHelpers.CreateUserAsync(factory);

        var response = await user.UploadAsync(Encoding.UTF8.GetBytes(TestFiles.Prose(60_000)), "long.txt"); // about 400 KB, over the JSON limit

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Parallel_uploads_beyond_the_per_user_cap_are_refused_with_429()
    {
        var gate = new GatedLimits();
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(
            cs, new Dictionary<string, string?>(NoWorker) { ["RateLimiting:Upload:MaxConcurrentPerUser"] = "1" },
            configureServices: services => { services.RemoveAll<IOptions<DocumentLimitsOptions>>(); services.AddSingleton<IOptions<DocumentLimitsOptions>>(gate); });
        var user = await DocumentTestHelpers.CreateUserAsync(factory);
        var other = await DocumentTestHelpers.CreateUserAsync(factory);

        gate.Arm();
        var first = user.UploadAsync(Encoding.UTF8.GetBytes("A slow upload."), "slow.txt");
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var seen = new List<string>();
        HttpResponseMessage? refused = null;
        for (var i = 0; i < 50 && refused is null; i++)
        {
            var attempt = await user.UploadAsync(Encoding.UTF8.GetBytes("Second upload."), "second.txt");
            seen.Add($"{(int)attempt.StatusCode}/{first.IsCompleted}");
            if (attempt.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused = attempt;
            }
            else
            {
                await Task.Delay(100); // the first request had not reached the limiter yet
            }
        }

        Assert.True(refused is not null, string.Join(",", seen.Take(5)));
        await AssertProblemAsync(refused!, HttpStatusCode.TooManyRequests);
        Assert.True(refused!.Headers.Contains("Retry-After"));

        // Another user is not affected by this user's slot.
        Assert.Equal(HttpStatusCode.Accepted, (await other.UploadAsync(Encoding.UTF8.GetBytes("Someone else."), "other.txt")).StatusCode);

        gate.Release.Set();
        Assert.Equal(HttpStatusCode.Accepted, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await user.UploadAsync(Encoding.UTF8.GetBytes("After release."), "third.txt")).StatusCode);
    }

    /// <summary>
    /// Holds the first upload inside the handler (the test server buffers request bodies, so a stalled body cannot do it): the limits are read
    /// after the rate limiter has admitted the request, and the armed read blocks until the test lets go.
    /// </summary>
    private sealed class GatedLimits : IOptions<DocumentLimitsOptions>
    {
        private readonly DocumentLimitsOptions _inner = new();
        private int _armed;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new(false);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public DocumentLimitsOptions Value
        {
            get
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    Started.TrySetResult();
                    Release.Wait(TimeSpan.FromSeconds(30));
                }

                return _inner;
            }
        }
    }

    // ---- A05: error responses ----

    [Fact]
    public async Task Malformed_requests_are_problem_details_not_server_errors()
    {
        await using var factory = await NewFactoryAsync();
        var user = await DocumentTestHelpers.CreateUserAsync(factory);

        var notJson = await user.Client.PostAsync("/api/v1/ask", new StringContent("{not json", Encoding.UTF8, "application/json"));
        await AssertProblemAsync(notJson, HttpStatusCode.BadRequest);

        var wrongType = await user.Client.PostAsync("/api/v1/ask", new StringContent("question=hi", Encoding.UTF8, "text/plain"));
        await AssertProblemAsync(wrongType, HttpStatusCode.UnsupportedMediaType);

        var empty = await user.Client.PostAsync("/api/v1/ask", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal("application/problem+json", empty.Content.Headers.ContentType?.MediaType);
        Assert.True((int)empty.StatusCode is 400 or 415);

        var wrongShape = await user.Client.PutAsJsonAsync("/api/v1/me/llm-key", new { provider = 5, model = new[] { 1 }, apiKey = true });
        await AssertProblemAsync(wrongShape, HttpStatusCode.BadRequest);

        await AssertProblemAsync(await user.Client.GetAsync("/api/v1/documents/not-a-guid"), HttpStatusCode.NotFound);
        await AssertProblemAsync(await user.Client.GetAsync("/no/such/route"), HttpStatusCode.NotFound);
        await AssertProblemAsync(await user.Client.PatchAsJsonAsync("/api/v1/documents", new { }), HttpStatusCode.MethodNotAllowed);
    }

    // ---- REL-2: the database layer retries transient failures ----

    [Fact]
    public async Task The_database_context_uses_a_retrying_execution_strategy()
    {
        await using var factory = await NewFactoryAsync();
        using var _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();

        Assert.True(strategy.RetriesOnFailure, "Aspire's EnrichNpgsqlDbContext should enable retries on transient Npgsql errors");
    }

    // ---- SEC-5 / storage: a document cannot produce an unbounded number of chunks ----

    [Fact]
    public async Task A_document_with_more_chunks_than_the_limit_fails_with_a_clear_reason_before_any_embedding()
    {
        _ = ModelLocator.Directory;
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(cs, new Dictionary<string, string?>
        {
            ["Documents:Ingestion:MaxChunksPerDocument"] = "3",
            ["Documents:Ingestion:PollIntervalSeconds"] = "1",
        });
        var user = await DocumentTestHelpers.CreateUserAsync(factory);

        var tooLong = await user.UploadOkAsync(Encoding.UTF8.GetBytes(TestFiles.Prose(3000)), "book.txt");
        var failed = await user.WaitForFinalStatusAsync(tooLong);

        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Contains("too long", failed.GetProperty("failureReason").GetString());
        Assert.Contains("3", failed.GetProperty("failureReason").GetString());
        await using var db = DocumentTestHelpers.OpenDb(cs);
        Assert.False(await db.Chunks.IgnoreQueryFilters().AnyAsync(c => c.DocumentId == tooLong));
        Assert.False(await db.DocumentUploads.AnyAsync(u => u.DocumentId == tooLong)); // staged bytes are discarded

        var ok = await user.UploadOkAsync(Encoding.UTF8.GetBytes("A short note about the office address."), "note.txt");
        Assert.Equal("Ready", (await user.WaitForFinalStatusAsync(ok)).GetProperty("status").GetString());
    }
}
