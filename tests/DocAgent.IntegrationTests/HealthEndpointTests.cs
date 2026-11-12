using System.Net;
using Npgsql;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class HealthEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Live_returns_healthy()
    {
        await using var factory = new DocAgentApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_returns_healthy_when_database_is_reachable()
    {
        await using var factory = new DocAgentApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_returns_unhealthy_when_database_is_unreachable()
    {
        // Port 1 on localhost: nothing listens there, so the connection fails fast.
        const string unreachable = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2";
        await using var factory = new DocAgentApiFactory(unreachable);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class PgvectorImageTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Vector_extension_can_be_created()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using (var create = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var query = new NpgsqlCommand(
            "SELECT '[1,2,3]'::vector <=> '[1,2,3]'::vector;", connection);
        var distance = Convert.ToDouble(await query.ExecuteScalarAsync());

        Assert.Equal(0.0, distance, precision: 6);
    }
}
