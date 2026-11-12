using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migration_enables_vector_extension_and_creates_embedding_column_and_hnsw_index()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        await using var db = TestDatabase.CreateContext(cs);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        Assert.Equal(1L, await Scalar(conn, "SELECT count(*) FROM pg_extension WHERE extname = 'vector'"));

        var columnType = await Scalar(conn, """
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.relname = 'Chunks' AND a.attname = 'Embedding'
            """);
        Assert.Equal("vector(384)", columnType);

        var indexDef = (string)(await Scalar(conn,
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'Chunks' AND indexdef ILIKE '%hnsw%'"))!;
        Assert.Contains("vector_cosine_ops", indexDef);
    }

    [Fact]
    public async Task Code_hash_has_a_unique_index()
    {
        var cs = await TestDatabase.CreateMigratedAsync(postgres);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();

        var def = (string)(await Scalar(conn,
            "SELECT indexdef FROM pg_indexes WHERE tablename = 'ExternalLoginCodes' AND indexdef ILIKE '%CodeHash%'"))!;
        Assert.Contains("UNIQUE", def);
    }

    private static async Task<object?> Scalar(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }
}
