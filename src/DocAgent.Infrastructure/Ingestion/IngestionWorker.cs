using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocAgent.Infrastructure.Ingestion;

/// <summary>Background service that processes queued ingestion jobs one at a time (DOC-4).</summary>
public sealed class IngestionWorker(
    IServiceScopeFactory scopes,
    IngestionSignal signal,
    IOptions<IngestionOptions> options,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // never block host startup

        // REL-3: resume or fail jobs that a previous run left Running. Retry until the database answers.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var recovered = await scope.ServiceProvider.GetRequiredService<IngestionJobQueue>().RecoverInterruptedAsync(stoppingToken);
                if (recovered > 0)
                {
                    logger.LogWarning("Recovered {Count} interrupted ingestion job(s).", recovered);
                }

                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not recover interrupted ingestion jobs; retrying.");
                await DelayAsync(ErrorDelay, stoppingToken);
            }
        }

        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));
        var staleFor = TimeSpan.FromMinutes(Math.Max(1, options.Value.StaleJobMinutes));
        var nextStaleCheck = DateTimeOffset.UtcNow + StaleCheckInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (DateTimeOffset.UtcNow >= nextStaleCheck)
                {
                    // REL-3 while running: a job can stay Running when its failure could not be written (database down at that moment).
                    nextStaleCheck = DateTimeOffset.UtcNow + StaleCheckInterval;
                    var reclaimed = await scope.ServiceProvider.GetRequiredService<IngestionJobQueue>().RecoverInterruptedAsync(stoppingToken, staleFor);
                    if (reclaimed > 0)
                    {
                        logger.LogWarning("Took back {Count} stale ingestion job(s).", reclaimed);
                    }
                }

                var claimed = await scope.ServiceProvider.GetRequiredService<IngestionJobQueue>().ClaimNextAsync(stoppingToken);
                if (claimed is null)
                {
                    try
                    {
                        await signal.WaitAsync(poll, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                var outcome = await scope.ServiceProvider.GetRequiredService<IngestionProcessor>().ProcessAsync(claimed, stoppingToken);
                if (outcome == IngestionOutcome.WillRetry)
                {
                    await DelayAsync(TimeSpan.FromSeconds(2 * claimed.Attempts), stoppingToken); // simple backoff
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ingestion worker loop failed; continuing.");
                await DelayAsync(ErrorDelay, stoppingToken);
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
