using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class FlagsCacheTests
{
    [Fact]
    public async Task RefreshAsync_runs_follow_up_refresh_with_highest_pending_version()
    {
        var firstRefresh = new TaskCompletionSource<FlagsCacheRefreshResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFetchStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int?>();

        Task<FlagsCacheRefreshResult?> Fetch(int? waitForVersion)
        {
            calls.Add(waitForVersion);
            if (calls.Count == 1)
            {
                firstFetchStarted.TrySetResult(null);
                return firstRefresh.Task;
            }

            return Task.FromResult<FlagsCacheRefreshResult?>(new FlagsCacheRefreshResult(
                CreateCollection("newest"),
                22));
        }

        var cache = new FlagsCache(Fetch, minRefreshInterval: TimeSpan.Zero);
        var refreshTask = cache.RefreshAsync(null, CancellationToken.None);
        await firstFetchStarted.Task;

        var refresh21Task = cache.RefreshAsync(21, CancellationToken.None);
        var refresh22Task = cache.RefreshAsync(22, CancellationToken.None);

        firstRefresh.SetResult(new FlagsCacheRefreshResult(CreateCollection("first"), 20));
        await refreshTask;
        await refresh21Task;
        await refresh22Task;

        Assert.Equal(new int?[] { null, 22 }, calls);
        var snapshot = cache.GetSnapshot()!;
        Assert.Equal("newest", snapshot.Collection.Definitions.Flags[0].Key);
        Assert.Equal(22, snapshot.FlagStateVersion);
        cache.Destroy();
    }

    [Fact]
    public async Task RefreshAsync_does_not_replace_newer_snapshot_with_older_version()
    {
        var queue = new Queue<FlagsCacheRefreshResult?>(
        [
            new FlagsCacheRefreshResult(CreateCollection("newer"), 30),
            new FlagsCacheRefreshResult(CreateCollection("older"), 20),
        ]);

        var cache = new FlagsCache(
            _ => Task.FromResult(queue.Dequeue()),
            minRefreshInterval: TimeSpan.Zero);

        await cache.RefreshAsync(null, CancellationToken.None);
        await cache.RefreshAsync(null, CancellationToken.None);

        var snapshot = cache.GetSnapshot()!;
        Assert.Equal("newer", snapshot.Collection.Definitions.Flags[0].Key);
        Assert.Equal(30, snapshot.FlagStateVersion);
        cache.Destroy();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 1, true)]
    [InlineData(1, null, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, false)]
    [InlineData(1, 2, true)]
    public async Task RefreshAsync_preserves_version_ordering_for_live_and_unversioned_snapshots(int? currentVersion, int? nextVersion, bool replace)
    {
        var current = new FlagsCacheRefreshResult(CreateCollection("current"), currentVersion);
        var next = new FlagsCacheRefreshResult(CreateCollection("next"), nextVersion);
        var responses = new Queue<FlagsCacheRefreshResult?>([current, next]);
        var cache = new FlagsCache(_ => Task.FromResult(responses.Dequeue()), minRefreshInterval: TimeSpan.Zero);
        try
        {
            await cache.RefreshAsync(null, CancellationToken.None);
            await cache.RefreshAsync(null, CancellationToken.None);

            Assert.Same(replace ? next : current, cache.GetSnapshot());
        }
        finally
        {
            cache.Destroy();
        }
    }

    [Fact]
    public async Task RefreshAsync_completes_earlier_call_without_waiting_for_later_refresh_requests()
    {
        var firstFetch = new TaskCompletionSource<FlagsCacheRefreshResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFetch = new TaskCompletionSource<FlagsCacheRefreshResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFetchStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFetchStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int?>();

        Task<FlagsCacheRefreshResult?> Fetch(int? waitForVersion)
        {
            calls.Add(waitForVersion);
            if (calls.Count == 1)
            {
                firstFetchStarted.TrySetResult(null);
                return firstFetch.Task;
            }

            if (calls.Count == 2)
            {
                secondFetchStarted.TrySetResult(null);
                return secondFetch.Task;
            }

            throw new InvalidOperationException("Unexpected fetch call.");
        }

        var cache = new FlagsCache(Fetch, minRefreshInterval: TimeSpan.Zero);
        var firstRefreshTask = cache.RefreshAsync(null, CancellationToken.None);
        await firstFetchStarted.Task;

        var secondRefreshTask = cache.RefreshAsync(null, CancellationToken.None);

        firstFetch.SetResult(new FlagsCacheRefreshResult(CreateCollection("first"), 1));

        await firstRefreshTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(secondRefreshTask.IsCompleted);

        await secondFetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(secondRefreshTask.IsCompleted);

        secondFetch.SetResult(new FlagsCacheRefreshResult(CreateCollection("second"), 2));
        await secondRefreshTask;

        Assert.Equal(new int?[] { null, null }, calls);
        Assert.Equal("second", cache.GetSnapshot()!.Collection.Definitions.Flags[0].Key);
        cache.Destroy();
    }

    [Fact]
    public async Task GetSnapshot_keeps_definitions_and_version_paired_during_refreshes()
    {
        var version = 0;
        var cache = new FlagsCache(_ =>
        {
            version++;
            return Task.FromResult<FlagsCacheRefreshResult?>(new FlagsCacheRefreshResult(CreateCollection($"version-{version}"), version));
        }, minRefreshInterval: TimeSpan.Zero);
        try
        {
            Assert.Null(cache.GetSnapshot());
            await cache.RefreshAsync(null, CancellationToken.None);
            var original = cache.GetSnapshot()!;
            var mismatches = 0;
            var refreshes = Task.Run(async () =>
            {
                for (var index = 0; index < 500; index++)
                {
                    await cache.RefreshAsync(null, CancellationToken.None);
                }
            });
            do
            {
                var snapshot = cache.GetSnapshot()!;
                if (snapshot.Collection.Definitions.Flags[0].Key != $"version-{snapshot.FlagStateVersion}")
                {
                    mismatches++;
                }
            }
            while (!refreshes.IsCompleted);

            await refreshes;
            Assert.Equal(0, mismatches);
            Assert.Equal("version-1", original.Collection.Definitions.Flags[0].Key);
            Assert.Equal(1, original.FlagStateVersion);
            Assert.Equal(501, cache.GetSnapshot()!.FlagStateVersion);
        }
        finally
        {
            cache.Destroy();
        }

        Assert.Null(cache.GetSnapshot());
    }

    private static PreparedFlagCollection CreateCollection(string key)
    {
        return PreparedFlagCollection.Prepare(
            [TestDefinitions.CreateFlag(key, 1, new FlagConstantFilterDefinition { Value = true })],
            []);
    }
}
