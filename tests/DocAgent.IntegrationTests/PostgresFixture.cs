using Testcontainers.PostgreSql;

namespace DocAgent.IntegrationTests;

/// <summary>
/// Starts one PostgreSQL + pgvector container shared by all tests in the collection.
/// Uses the same image as the Aspire AppHost so tests match local development.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Image = "pgvector/pgvector:pg17";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase("docagentdb")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
