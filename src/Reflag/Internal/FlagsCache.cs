using Microsoft.Extensions.Logging;

namespace Reflag.Internal;

internal sealed record FlagsCacheRefreshResult(
    CompiledFlagDefinition[] Definitions,
    int? FlagStateVersion);

internal sealed class FlagsCache
{
    private readonly object _gate = new();
    private readonly Func<int?, Task<FlagsCacheRefreshResult?>> _fetchFlags;
    private readonly ILogger _logger;
    private readonly TimeSpan _minRefreshInterval;
    private readonly CancellationTokenSource _destroyCancellationTokenSource = new();

    private readonly List<RefreshWaiter> _waiters = [];

    private CompiledFlagDefinition[]? _value;
    private int? _flagStateVersion;
    private Task? _drainTask;
    private DateTimeOffset? _lastRefreshAt;
    private DateTimeOffset? _lastRefreshStartedAt;
    private bool _destroyed;
    private bool _pendingFullRefresh;
    private int? _pendingWaitForVersion;
    private long? _activeRefreshTicket;
    private long? _pendingRefreshTicket;
    private long _nextRefreshTicket = 1;

    public FlagsCache(
        Func<int?, Task<FlagsCacheRefreshResult?>> fetchFlags,
        ILogger? logger = null,
        TimeSpan? minRefreshInterval = null)
    {
        _fetchFlags = fetchFlags;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _minRefreshInterval = minRefreshInterval ?? ReflagConstants.MinRefreshInterval;
    }

    public CompiledFlagDefinition[]? Get()
    {
        lock (_gate)
        {
            return _value;
        }
    }

    public DateTimeOffset? GetLastRefreshAt()
    {
        lock (_gate)
        {
            return _lastRefreshAt;
        }
    }

    public async Task<CompiledFlagDefinition[]?> RefreshAsync(int? waitForVersion, CancellationToken cancellationToken)
    {
        Task? waiterTask;
        lock (_gate)
        {
            if (_destroyed)
            {
                return _value;
            }

            if (waitForVersion is not null && _flagStateVersion is not null && _flagStateVersion >= waitForVersion)
            {
                return _value;
            }

            QueueRefreshNoLock(waitForVersion);
            var targetRefreshTicket = EnsurePendingRefreshTicketNoLock();
            waiterTask = targetRefreshTicket is null ? null : EnqueueWaiterNoLock(targetRefreshTicket.Value);
            EnsureDrainStartedNoLock();
        }

        if (waiterTask is not null)
        {
            await AsyncHelpers.WaitAsync(waiterTask, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            return _value;
        }
    }

    public Task WaitForRefreshAsync(CancellationToken cancellationToken)
    {
        Task? waiterTask;
        lock (_gate)
        {
            if (_destroyed)
            {
                return Task.CompletedTask;
            }

            var targetRefreshTicket = _pendingRefreshTicket ?? _activeRefreshTicket;
            if (targetRefreshTicket is null)
            {
                return Task.CompletedTask;
            }

            waiterTask = EnqueueWaiterNoLock(targetRefreshTicket.Value);
            EnsureDrainStartedNoLock();
        }

        return AsyncHelpers.WaitAsync(waiterTask, cancellationToken);
    }

    public void Destroy()
    {
        List<RefreshWaiter> waiters;
        lock (_gate)
        {
            if (_destroyed)
            {
                return;
            }

            _destroyed = true;
            _value = null;
            _flagStateVersion = null;
            _drainTask = null;
            _pendingFullRefresh = false;
            _pendingWaitForVersion = null;
            _lastRefreshAt = null;
            _lastRefreshStartedAt = null;
            _activeRefreshTicket = null;
            _pendingRefreshTicket = null;
            waiters = _waiters.ToList();
            _waiters.Clear();
        }

        _destroyCancellationTokenSource.Cancel();
        foreach (var waiter in waiters)
        {
            waiter.CompletionSource.TrySetResult(null);
        }
    }

    private void QueueRefreshNoLock(int? waitForVersion)
    {
        if (waitForVersion is not null)
        {
            if (_flagStateVersion is not null && _flagStateVersion >= waitForVersion)
            {
                return;
            }

            _pendingWaitForVersion = _pendingWaitForVersion is null
                ? waitForVersion
                : Math.Max(_pendingWaitForVersion.Value, waitForVersion.Value);
            _pendingFullRefresh = false;
            return;
        }

        if (_pendingWaitForVersion is not null)
        {
            return;
        }

        _pendingFullRefresh = true;
    }

    private bool HasPendingRefreshNoLock()
    {
        return _pendingFullRefresh || _pendingWaitForVersion is not null;
    }

    private long? EnsurePendingRefreshTicketNoLock()
    {
        if (!HasPendingRefreshNoLock())
        {
            return _pendingRefreshTicket ?? _activeRefreshTicket;
        }

        _pendingRefreshTicket ??= _nextRefreshTicket++;
        return _pendingRefreshTicket;
    }

    private RefreshWorkItem? TakeNextRefreshWorkItemNoLock()
    {
        if (_pendingRefreshTicket is null)
        {
            return null;
        }

        var request = TakeNextRefreshRequestNoLock();
        if (request is null)
        {
            return null;
        }

        var ticket = _pendingRefreshTicket.Value;
        _pendingRefreshTicket = null;
        _activeRefreshTicket = ticket;
        return new RefreshWorkItem(ticket, request.Value.WaitForVersion);
    }

    private RefreshRequest? TakeNextRefreshRequestNoLock()
    {
        if (_pendingWaitForVersion is not null)
        {
            var waitForVersion = _pendingWaitForVersion;
            _pendingWaitForVersion = null;
            return new RefreshRequest(waitForVersion);
        }

        if (!_pendingFullRefresh)
        {
            return null;
        }

        _pendingFullRefresh = false;
        return new RefreshRequest(null);
    }

    private bool ShouldApplyRefreshResultNoLock(int? flagStateVersion)
    {
        if (flagStateVersion is null)
        {
            return _flagStateVersion is null;
        }

        return _flagStateVersion is null || flagStateVersion >= _flagStateVersion;
    }

    private void ClearSatisfiedPendingVersionNoLock(int? seenFlagStateVersion)
    {
        if (_pendingWaitForVersion is null)
        {
            return;
        }

        var latestKnownVersion = Math.Max(_flagStateVersion ?? -1, seenFlagStateVersion ?? -1);
        if (latestKnownVersion >= _pendingWaitForVersion)
        {
            _pendingWaitForVersion = null;
        }
    }

    private TimeSpan GetNextRefreshDelayNoLock()
    {
        if (_lastRefreshStartedAt is null || _minRefreshInterval <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var dueAt = _lastRefreshStartedAt.Value + _minRefreshInterval;
        var delay = dueAt - DateTimeOffset.UtcNow;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private Task EnqueueWaiterNoLock(long targetRefreshTicket)
    {
        var completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add(new RefreshWaiter(targetRefreshTicket, completionSource));
        return completionSource.Task;
    }

    private void EnsureDrainStartedNoLock()
    {
        if (_destroyed || _drainTask is not null || !HasPendingRefreshNoLock())
        {
            return;
        }

        _drainTask = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            RefreshWorkItem workItem;
            TimeSpan delay;
            lock (_gate)
            {
                if (_destroyed)
                {
                    _drainTask = null;
                    return;
                }

                var nextWorkItem = TakeNextRefreshWorkItemNoLock();
                if (nextWorkItem is null)
                {
                    _drainTask = null;
                    return;
                }

                workItem = nextWorkItem.Value;
                delay = GetNextRefreshDelayNoLock();
            }

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _destroyCancellationTokenSource.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_destroyCancellationTokenSource.IsCancellationRequested)
            {
                lock (_gate)
                {
                    _drainTask = null;
                }

                return;
            }

            lock (_gate)
            {
                if (_destroyed)
                {
                    _drainTask = null;
                    return;
                }

                _lastRefreshStartedAt = DateTimeOffset.UtcNow;
            }

            await FetchAndApplyRefreshAsync(workItem.WaitForVersion).ConfigureAwait(false);

            lock (_gate)
            {
                if (_destroyed)
                {
                    _drainTask = null;
                    return;
                }

                if (_activeRefreshTicket == workItem.Ticket)
                {
                    _activeRefreshTicket = null;
                }

                CompleteSatisfiedWaitersNoLock(workItem.Ticket);
                if (!HasPendingRefreshNoLock())
                {
                    _drainTask = null;
                    return;
                }
            }
        }
    }

    private void CompleteSatisfiedWaitersNoLock(long completedRefreshTicket)
    {
        for (var index = _waiters.Count - 1; index >= 0; index--)
        {
            var waiter = _waiters[index];
            if (waiter.TargetRefreshTicket > completedRefreshTicket)
            {
                continue;
            }

            _waiters.RemoveAt(index);
            waiter.CompletionSource.TrySetResult(null);
        }
    }

    private async Task FetchAndApplyRefreshAsync(int? waitForVersion)
    {
        var result = await _fetchFlags(waitForVersion).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        var applied = false;
        lock (_gate)
        {
            if (_destroyed)
            {
                return;
            }

            ClearSatisfiedPendingVersionNoLock(result.FlagStateVersion);
            if (!ShouldApplyRefreshResultNoLock(result.FlagStateVersion))
            {
                return;
            }

            _value = result.Definitions;
            _flagStateVersion = result.FlagStateVersion;
            _lastRefreshAt = DateTimeOffset.UtcNow;
            applied = true;
        }

        if (applied)
        {
            _logger.LogInformation("refreshed flag definitions");
        }
    }

    private readonly record struct RefreshRequest(int? WaitForVersion);

    private readonly record struct RefreshWorkItem(long Ticket, int? WaitForVersion);

    private sealed record RefreshWaiter(long TargetRefreshTicket, TaskCompletionSource<object?> CompletionSource);
}
