namespace Clay;

internal sealed class AsyncOperationQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _latestOperation;

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

    public void CancelLatest()
    {
        lock (_sync) _latestOperation?.Cancel();
    }

    public async Task RunLatestAsync(Func<CancellationToken, Task> operation)
    {
        CancellationTokenSource current;
        lock (_sync)
        {
            _latestOperation?.Cancel();
            current = new CancellationTokenSource();
            _latestOperation = current;
        }

        try
        {
            await _gate.WaitAsync(current.Token);
            try
            {
                await operation(current.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_latestOperation, current)) _latestOperation = null;
            }
            current.Dispose();
        }
    }
}
