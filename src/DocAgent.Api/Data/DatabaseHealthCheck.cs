using DocAgent.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocAgent.Api.Data;

/// <summary>
/// Readiness check: opens a plain connection and runs SELECT 1. It deliberately bypasses EF's
/// retrying execution strategy (enabled by the Aspire integration), which would otherwise make an
/// unreachable database block the probe for over a minute instead of failing fast.
/// </summary>
public sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(cancellationToken);
            }
            finally
            {
                await connection.CloseAsync();
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is unreachable.", ex);
        }
    }
}
