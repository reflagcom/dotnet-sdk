using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Reflag.Tests;

public sealed class EvaluationSnapshotTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialization_during_a_read_does_not_attach_a_new_version_to_old_results(bool bootstrap)
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(TestDefinitions.FlagsResponse(7, TestDefinitions.CreateMultivariateFlag(
            "model", "quality", new Dictionary<string, object?> { ["quality"] = "large" })));
        var logger = new BlockingMissingSnapshotLogger();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            FlagsFetchRetries = 0,
            Logger = logger,
        });
        var read = Task.Run(() =>
        {
            if (bootstrap)
            {
                return client.GetFlagsForBootstrap(new ReflagContext());
            }

            Assert.Equal("fallback", client.GetFlag("model", new ReflagContext(), "fallback"));
            return null;
        });
        try
        {
            // The warning occurs after capturing an empty cache. Publish a live snapshot before the read resumes.
            await logger.SnapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await client.InitializeAsync();
        }
        finally
        {
            logger.ResumeRead.TrySetResult();
        }

        var payload = await read.WaitAsync(TimeSpan.FromSeconds(5));
        await client.FlushAsync();
        if (bootstrap)
        {
            Assert.NotNull(payload);
            Assert.Empty(payload.Flags);
            Assert.Null(payload.FlagStateVersion);
        }
        else
        {
            using var bulk = JsonDocument.Parse(Assert.Single(transport.PostCalls).Body);
            var check = Assert.Single(bulk.RootElement.EnumerateArray());
            Assert.Equal("feature-flag-event", check.GetProperty("type").GetString());
            Assert.False(check.TryGetProperty("flagStateVersion", out _));
            Assert.False(check.TryGetProperty("evalResult", out _));
            Assert.Equal("CLIENT_NOT_INITIALIZED", check.GetProperty("evalErrors")[0].GetProperty("code").GetString());
        }

        Assert.Equal(7, client.GetFlagsForBootstrap(new ReflagContext()).FlagStateVersion);
        Assert.Equal("large", client.GetFlag("model", new ReflagContext(), "fallback"));
    }

    private sealed class BlockingMissingSnapshotLogger : ILogger
    {
        public TaskCompletionSource SnapshotRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ResumeRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning && formatter(state, exception) == "no flag definitions available, using flag overrides only.")
            {
                SnapshotRead.TrySetResult();
                ResumeRead.Task.GetAwaiter().GetResult();
            }
        }
    }
}
