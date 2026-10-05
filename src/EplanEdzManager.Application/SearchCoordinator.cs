namespace EplanEdzManager.Application;

public sealed class SearchCoordinator : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly object _sync = new();
    private CancellationTokenSource? _current;
    private bool _disposed;

    public SearchCoordinator(TimeSpan? delay = null)
    {
        _delay = delay ?? TimeSpan.FromMilliseconds(300);
    }

    public async Task<T?> ScheduleAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource local;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current?.Cancel();
            _current?.Dispose();
            _current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            local = _current;
        }

        try
        {
            await Task.Delay(_delay, local.Token).ConfigureAwait(false);
            return await operation(local.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && local.IsCancellationRequested)
        {
            return default;
        }
    }

    public void Cancel()
    {
        lock (_sync) _current?.Cancel();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
        }
    }
}
