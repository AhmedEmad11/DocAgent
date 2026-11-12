namespace DocAgent.Infrastructure.Ingestion;

/// <summary>Wakes the ingestion worker when a job is queued so uploads start processing without waiting for the next poll.</summary>
public sealed class IngestionSignal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        if (_semaphore.CurrentCount == 0)
        {
            try
            {
                _semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signalled
            }
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _semaphore.WaitAsync(timeout, ct);
}
