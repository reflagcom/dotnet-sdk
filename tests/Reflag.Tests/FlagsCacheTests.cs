using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class FlagsCacheTests
{
    [Fact]
    public async Task RefreshAsync_runs_follow_up_refresh_with_highest_pending_version()
    {
        var firstRefresh = new TaskCompletionSource<FlagsCacheRefreshResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int?>();

        Task<FlagsCacheRefreshResult?> Fetch(int? waitForVersion)
        {
            calls.Add(waitForVersion);
            if (calls.Count == 1)
            {
                return firstRefresh.Task;
            }

            return Task.FromResult<FlagsCacheRefreshResult?>(new FlagsCacheRefreshResult(
                [CreateCompiledDefinition("newest")],
                22));
        }

        var cache = new FlagsCache(Fetch, minRefreshInterval: TimeSpan.Zero);
        var refreshTask = cache.RefreshAsync(null, CancellationToken.None);

        await Task.Delay(10);
        var refresh21Task = cache.RefreshAsync(21, CancellationToken.None);
        var refresh22Task = cache.RefreshAsync(22, CancellationToken.None);

        firstRefresh.SetResult(new FlagsCacheRefreshResult([CreateCompiledDefinition("first")], 20));
        await refreshTask;
        await refresh21Task;
        await refresh22Task;

        Assert.Equal(new int?[] { null, 22 }, calls);
        Assert.Equal("newest", cache.Get()![0].Definition.Key);
        cache.Destroy();
    }

    [Fact]
    public async Task RefreshAsync_does_not_replace_newer_snapshot_with_older_version()
    {
        var queue = new Queue<FlagsCacheRefreshResult?>(
        [
            new FlagsCacheRefreshResult([CreateCompiledDefinition("newer")], 30),
            new FlagsCacheRefreshResult([CreateCompiledDefinition("older")], 20),
        ]);

        var cache = new FlagsCache(
            _ => Task.FromResult(queue.Dequeue()),
            minRefreshInterval: TimeSpan.Zero);

        await cache.RefreshAsync(null, CancellationToken.None);
        await cache.RefreshAsync(null, CancellationToken.None);

        Assert.Equal("newer", cache.Get()![0].Definition.Key);
        cache.Destroy();
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

        firstFetch.SetResult(new FlagsCacheRefreshResult([CreateCompiledDefinition("first")], 1));

        var completed = await Task.WhenAny(firstRefreshTask, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(firstRefreshTask, completed);
        Assert.True(firstRefreshTask.IsCompletedSuccessfully);
        Assert.False(secondRefreshTask.IsCompleted);

        var secondFetchStartedCompleted = await Task.WhenAny(secondFetchStarted.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(secondFetchStarted.Task, secondFetchStartedCompleted);
        Assert.False(secondRefreshTask.IsCompleted);

        secondFetch.SetResult(new FlagsCacheRefreshResult([CreateCompiledDefinition("second")], 2));
        await secondRefreshTask;

        Assert.Equal(new int?[] { null, null }, calls);
        Assert.Equal("second", cache.Get()![0].Definition.Key);
        cache.Destroy();
    }

    private static CompiledFlagDefinition CreateCompiledDefinition(string key)
    {
        return new CompiledFlagDefinition
        {
            Definition = TestDefinitions.CreateFlag(key, 1, new FlagConstantFilterDefinition { Value = true }),
            Evaluator = FlagEvaluation.NewEvaluator(
            [
                new EvaluationRule<bool>(new CompiledConstantFilter(true), true),
            ]),
        };
    }
}
