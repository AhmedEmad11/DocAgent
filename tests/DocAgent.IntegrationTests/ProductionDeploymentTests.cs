using System.Net;
using DocAgent.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAgent.IntegrationTests;

/// <summary>Phase 8: what the Production environment demands and does not do (docs/DEPLOYMENT.md).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ProductionDeploymentTests(PostgresFixture postgres)
{
    private static Dictionary<string, string?> ProductionSettings(Action<Dictionary<string, string?>>? change = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "docagent.example.test;localhost",
            ["DataProtection:CertificatePfxBase64"] = DataProtectionTests.NewPfxBase64(),
            ["DataProtection:CertificatePassword"] = DataProtectionTests.Password,
            ["Llm:Demo:OpenRouterApiKey"] = "test-operator-key",
            ["Documents:Ingestion:WorkerEnabled"] = "false",
        };
        change?.Invoke(settings);
        return settings;
    }

    private async Task<DocAgentApiFactory> ProductionFactoryAsync(Action<Dictionary<string, string?>>? change = null) =>
        new(await TestDatabase.CreateAsync(postgres), ProductionSettings(change), environment: "Production");

    [Fact]
    public async Task Production_starts_with_the_certificate_but_does_not_migrate_the_database()
    {
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(cs, ProductionSettings(), environment: "Production");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", conn);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync()); // migrations are the deploy step (--migrate), never startup
    }

    [Fact]
    public async Task Production_fails_at_startup_without_the_data_protection_certificate()
    {
        await using var factory = await ProductionFactoryAsync(s => s.Remove("DataProtection:CertificatePfxBase64"));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("DataProtection:CertificatePfxBase64", Flatten(ex));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("*.example.test")]
    public async Task Production_fails_at_startup_when_allowed_hosts_is_open(string allowedHosts)
    {
        await using var factory = await ProductionFactoryAsync(s => s["AllowedHosts"] = allowedHosts);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("AllowedHosts", Flatten(ex));
    }

    [Fact]
    public async Task Production_rejects_a_forged_host_header_and_trusts_forwarded_headers_from_the_ingress()
    {
        await using var factory = await ProductionFactoryAsync();
        using var client = factory.CreateClient();

        using var forged = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        forged.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(forged)).StatusCode);

        // Probes send the app's FQDN as Host (infra/main.bicep); over http without X-Forwarded-Proto there is no redirect (HttpsRedirection is off).
        using var probe = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        probe.Headers.Host = "docagent.example.test";
        var probeResponse = await client.SendAsync(probe);
        Assert.Equal(HttpStatusCode.OK, probeResponse.StatusCode);
        Assert.False(probeResponse.Headers.Contains("Strict-Transport-Security"));

        // Through the ingress: X-Forwarded-Proto=https is honoured, so HSTS is sent.
        using var viaIngress = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        viaIngress.Headers.Host = "docagent.example.test";
        viaIngress.Headers.Add("X-Forwarded-Proto", "https");
        viaIngress.Headers.Add("X-Forwarded-For", "203.0.113.7");
        Assert.True((await client.SendAsync(viaIngress)).Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Migrate_command_applies_all_migrations_and_is_repeatable()
    {
        var cs = await TestDatabase.CreateAsync(postgres);

        var first = new StringWriter();
        Assert.Equal(0, await MigrateCommand.RunAsync(cs, first));
        Assert.Contains("Migrations applied", first.ToString());

        await using (var db = TestDatabase.CreateContext(cs))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await db.DataProtectionKeys.CountAsync()); // table exists
        }

        var second = new StringWriter();
        Assert.Equal(0, await MigrateCommand.RunAsync(cs, second));
        Assert.Contains("up to date", second.ToString());
    }

    [Fact]
    public async Task Migrate_command_reads_the_connection_string_from_the_environment_style_setting_and_reports_failures_without_the_password()
    {
        var none = new StringWriter();
        Assert.Equal(2, await MigrateCommand.RunAsync(connectionString: null, none));

        var failed = new StringWriter();
        Assert.Equal(1, await MigrateCommand.RunAsync("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=s3cret-pw-xyz;Timeout=2", failed));
        Assert.DoesNotContain("s3cret-pw-xyz", failed.ToString());

        Assert.True(MigrateCommand.IsRequested(["--migrate"]));
        Assert.False(MigrateCommand.IsRequested([]));

        var cs = await TestDatabase.CreateAsync(postgres);
        var viaArgs = new StringWriter();
        Assert.Equal(0, await MigrateCommand.RunAsync(["--migrate", $"--ConnectionStrings:docagentdb={cs}"], viaArgs));
        await using var db = TestDatabase.CreateContext(cs);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            messages.Add(e.Message);
            if (e.InnerException is null) { break; }
        }

        return string.Join(" | ", messages);
    }
}
