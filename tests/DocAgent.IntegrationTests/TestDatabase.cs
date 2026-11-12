using DocAgent.Core.Abstractions;
using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocAgent.IntegrationTests;

/// <summary>Creates isolated, empty databases on the shared PostgreSQL container.</summary>
public static class TestDatabase
{
    public static async Task<string> CreateAsync(PostgresFixture postgres)
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>Creates a database and applies all migrations.</summary>
    public static async Task<string> CreateMigratedAsync(PostgresFixture postgres)
    {
        var cs = await CreateAsync(postgres);
        await using var db = CreateContext(cs);
        await db.Database.MigrateAsync();
        return cs;
    }

    public static AppDbContext CreateContext(string connectionString, Guid? userId = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;
        return new AppDbContext(options, new FixedUser(userId));
    }

    private sealed class FixedUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
    }
}
