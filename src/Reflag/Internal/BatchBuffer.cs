using Microsoft.Extensions.Logging;

namespace Reflag.Internal;

internal sealed class BatchBuffer<T>
{
    private readonly object _gate = new();
    private readonly Func<IReadOnlyList<T>, CancellationToken, Task> _flushHandler;
    private readonly ILogger _logger;
    private readonly int _maxSize;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);

    private List<T> _buffer = new();
    private Timer? _timer;
    private bool _destroyed;

    public BatchBuffer(
        Func<IReadOnlyList<T>, CancellationToken, Task> flushHandler,
        ILogger? logger,
        int maxSize,
        TimeSpan interval)
    {
        if (maxSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize), "maxSize must be greater than zero.");
        }

        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "interval must be greater than or equal to zero.");
        }

        _flushHandler = flushHandler;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _maxSize = maxSize;
        _interval = interval;
    }

    public async Task AddAsync(T item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<T>? snapshot = null;
        lock (_gate)
        {
            if (_destroyed)
            {
                return;
            }

            _buffer.Add(item);
            if (_buffer.Count >= _maxSize)
            {
                snapshot = TakeSnapshotNoLock();
            }
            else
            {
                EnsureTimerNoLock();
            }
        }

        if (snapshot is not null)
        {
            await FlushSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<T>? snapshot;
        lock (_gate)
        {
            snapshot = TakeSnapshotNoLock();
        }

        if (snapshot.Count == 0)
        {
            _logger.LogDebug("buffer is empty. nothing to flush");
            return;
        }

        await FlushSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    public void Destroy()
    {
        lock (_gate)
        {
            _destroyed = true;
            _buffer.Clear();
            StopTimerNoLock();
        }
    }

    private void EnsureTimerNoLock()
    {
        if (_timer is not null || _interval == TimeSpan.Zero)
        {
            return;
        }

        _timer = new Timer(
            static async state =>
            {
                if (state is BatchBuffer<T> buffer)
                {
                    await buffer.OnTimerAsync().ConfigureAwait(false);
                }
            },
            this,
            _interval,
            Timeout.InfiniteTimeSpan);
    }

    private async Task OnTimerAsync()
    {
        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
    }

    private List<T> TakeSnapshotNoLock()
    {
        StopTimerNoLock();
        if (_buffer.Count == 0)
        {
            return new List<T>();
        }

        var snapshot = _buffer;
        _buffer = new List<T>();
        return snapshot;
    }

    private void StopTimerNoLock()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private async Task FlushSnapshotAsync(IReadOnlyList<T> snapshot, CancellationToken cancellationToken)
    {
        await _flushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _flushHandler(snapshot, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("flushed buffered items (count={Count})", snapshot.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "flush of buffered items failed; discarding items (count={Count})", snapshot.Count);
            }
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }
}
