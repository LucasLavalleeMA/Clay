namespace Clay;

internal sealed class AsyncOperationQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Func<Task> operation)
    {
        await _gate.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _gate.Release();
        }
    }
}
