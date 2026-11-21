using System.Net;
using System.Net.Sockets;
using DocAgent.Api.Data;
using DocAgent.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAgent.IntegrationTests;

/// <summary>Hosts that assign the port and have no pre-deploy hook (SnapDeploy, docs/DEPLOYMENT.md): PORT and Database:MigrateOnStartup.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SnapDeployHostingTests(PostgresFixture postgres)
{
    private static Dictionary<string, string?> ProductionSettings(Action<Dictionary<string, string?>>? change = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "docagent.containers.snapdeploy.app;localhost",
            ["DataProtection:CertificatePfxBase64"] = DataProtectionTests.NewPfxBase64(),
            ["DataProtection:CertificatePassword"] = DataProtectionTests.Password,
            ["Llm:Demo:OpenRouterApiKey"] = "test-operator-key",
            ["Documents:Ingestion:WorkerEnabled"] = "false",
        };
        change?.Invoke(settings);
        return settings;
    }

    // ---- PORT ----

    [Fact]
    public void Port_is_not_set_by_default()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration["PORT"] = null;

        Assert.Null(ListenPort.Apply(builder.Configuration));
        Assert.Equal(8080, ListenPort.Default);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("80.5")]
    public void An_invalid_PORT_stops_the_startup_with_a_clear_message(string value)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration["PORT"] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => { ListenPort.Apply(builder.Configuration); });
        Assert.Contains("PORT must be a port number", ex.Message);
    }

    [Fact]
    public async Task The_app_listens_on_PORT_when_it_is_set()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration["PORT"] = port.ToString();
        builder.Configuration["HTTP_PORTS"] = "8080"; // what the aspnet image sets; PORT must win
        Assert.Equal(port, ListenPort.Apply(builder.Configuration));
        var app = builder.Build();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            Assert.Equal("pong", await client.GetStringAsync($"http://127.0.0.1:{port}/ping"));
            Assert.All(app.Urls, url => Assert.EndsWith($":{port}", url));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // ---- Database:MigrateOnStartup ----

    [Fact]
    public async Task MigrateOnStartup_applies_the_migrations_when_Production_starts()
    {
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var factory = new DocAgentApiFactory(cs, ProductionSettings(s => s["Database:MigrateOnStartup"] = "true"), environment: "Production");
        using var client = factory.CreateClient();

        using var ready = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        ready.Headers.Host = "docagent.containers.snapdeploy.app";
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ready)).StatusCode);

        await using var db = TestDatabase.CreateContext(cs);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task MigrateOnStartup_fails_the_startup_clearly_when_the_database_cannot_be_reached_and_never_prints_the_password()
    {
        const string unreachable = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=example-only-not-a-secret;Timeout=2";
        await using var factory = new DocAgentApiFactory(unreachable, ProductionSettings(s => s["Database:MigrateOnStartup"] = "true"), environment: "Production");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var text = Flatten(ex);
        Assert.Contains("applying the database migrations failed", text);
        Assert.Contains("Database:MigrateOnStartup", text);
        Assert.DoesNotContain("example-only-not-a-secret", text);
    }

    [Fact]
    public async Task Parallel_starts_migrate_once_under_the_advisory_lock()
    {
        var cs = await TestDatabase.CreateAsync(postgres);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => MigrationRunner.ApplyAsync(cs, TimeSpan.FromMinutes(2))));

        Assert.Single(results, r => r.Count > 0); // exactly one instance applied them; the others found nothing pending
        await using var db = TestDatabase.CreateContext(cs);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task A_start_waits_while_another_instance_holds_the_migration_lock_and_gives_up_after_the_timeout()
    {
        var cs = await TestDatabase.CreateAsync(postgres);
        await using var holder = new NpgsqlConnection(cs);
        await holder.OpenAsync();
        await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({MigrationRunner.AdvisoryLockKey})", holder))
        {
            await take.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MigrationRunner.ApplyAsync(cs, TimeSpan.FromSeconds(2)));

        await using (var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({MigrationRunner.AdvisoryLockKey})", holder))
        {
            await release.ExecuteNonQueryAsync();
        }

        Assert.NotEmpty(await MigrationRunner.ApplyAsync(cs, TimeSpan.FromMinutes(2)));
    }

    // ---- TLS ended at the host's edge ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AssumeHttps_makes_requests_without_X_Forwarded_Proto_count_as_https(bool assumeHttps, bool expectHsts)
    {
        await using var factory = new DocAgentApiFactory(
            await TestDatabase.CreateAsync(postgres),
            ProductionSettings(s => s["ForwardedHeaders:AssumeHttps"] = assumeHttps ? "true" : "false"),
            environment: "Production");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Host = "docagent.containers.snapdeploy.app";
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectHsts, response.Headers.Contains("Strict-Transport-Security"));
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
