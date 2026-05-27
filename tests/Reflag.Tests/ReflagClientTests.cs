using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class ReflagClientTests
{
    [Fact]
    public async Task Client_allows_a_missing_ILogger()
    {
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
        });

        await client.InitializeAsync();

        var enabled = client.GetFlag("missing-flag", new ReflagContext(), new ReflagTelemetryOptions
        {
            EnableTelemetry = false,
        });

        Assert.False(enabled);
    }

    [Fact]
    public async Task AddReflag_registers_client_and_initializes_it_via_hosted_service()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson("{\"success\":true,\"flagStateVersion\":1,\"features\":[]}");

        var services = new ServiceCollection();
        services.AddReflag(_ => new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            FlagsFetchRetries = 0,
        });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ReflagClient>();
        var hostedService = provider.GetServices<IHostedService>().Single();

        await hostedService.StartAsync(CancellationToken.None);

        Assert.Single(transport.GetCalls);
        Assert.Same(client, provider.GetRequiredService<ReflagClient>());
    }

    [Fact]
    public async Task AddReflag_hosted_service_stop_flushes_buffered_events_by_default()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson("{\"success\":true,\"flagStateVersion\":1,\"features\":[]}");

        var services = new ServiceCollection();
        services.AddReflag(_ => new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            FlagsFetchRetries = 0,
        });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ReflagClient>();
        var hostedService = provider.GetServices<IHostedService>().Single();

        await hostedService.StartAsync(CancellationToken.None);
        await client.UpdateUserAsync("user-123");

        Assert.Empty(transport.PostCalls);

        await hostedService.StopAsync(CancellationToken.None);

        Assert.Single(transport.PostCalls);
        Assert.Equal("https://api.example.com/bulk", transport.PostCalls.Single().Url.ToString());
    }

    [Fact]
    public async Task AddReflag_options_overload_registers_client_and_provider_disposal_disposes_client()
    {
        var options = new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            Offline = true,
        };

        var services = new ServiceCollection();
        services.AddReflag(options);

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ReflagClient>();
        var hostedService = provider.GetServices<IHostedService>().Single();

        await hostedService.StartAsync(CancellationToken.None);
        await provider.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => client.GetFlagDefinitions());
    }

    [Fact]
    public async Task DisposeAsync_flushes_buffered_events_by_default()
    {
        var transport = new TestTransport();
        var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
        });

        await client.UpdateUserAsync("user-123");

        Assert.Empty(transport.PostCalls);

        await client.DisposeAsync();

        Assert.Single(transport.PostCalls);
        Assert.Equal("https://api.example.com/bulk", transport.PostCalls.Single().Url.ToString());
    }

    [Fact]
    public async Task DisposeAsync_skips_shutdown_flush_when_FlushOnExit_is_disabled()
    {
        var transport = new TestTransport();
        var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            Batch = new ReflagBatchOptions
            {
                FlushOnExit = false,
            },
        });

        await client.UpdateUserAsync("user-123");
        await client.DisposeAsync();

        Assert.Empty(transport.PostCalls);
    }

    [Fact]
    public async Task ReflagClient_public_methods_throw_after_disposal()
    {
        var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
        });

        await client.DisposeAsync();

        await AssertDisposedAsync(() => client.InitializeAsync());
        await AssertDisposedAsync(() => client.FlushAsync());
        await AssertDisposedAsync(() => client.RefreshFlagsAsync());
        AssertDisposed(() => client.GetFlagDefinitions());
        AssertDisposed(() => client.GetFlag("missing-flag", new ReflagContext()));
        AssertDisposed(() => client.GetFlagsForBootstrap(new ReflagContext()));
        AssertDisposed(() => client.BindClient(new ReflagContext()));
        AssertDisposed(() => client.BindClient(new { User = new { Id = "user-123" } }));
        await AssertDisposedAsync(() => client.UpdateUserAsync("user-123"));
        await AssertDisposedAsync(() => client.UpdateCompanyAsync("company-123"));
        await AssertDisposedAsync(() => client.TrackAsync("user-123", "checkout"));
        AssertDisposed(() => client.SetFlagOverrides(new Dictionary<string, bool> { ["flag-a"] = true }));
        AssertDisposed(() => client.SetFlagOverrides(static _ => new Dictionary<string, bool> { ["flag-a"] = true }));
        AssertDisposed(() => client.PushFlagOverrides(new Dictionary<string, bool> { ["flag-a"] = true }));
        AssertDisposed(() => client.PushFlagOverrides(static _ => new Dictionary<string, bool> { ["flag-a"] = true }));
        AssertDisposed(() => client.ClearFlagOverrides());
    }

    [Fact]
    public async Task ReflagBoundClient_methods_throw_after_root_client_is_disposed()
    {
        var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
        });

        var bound = client.BindClient(
            new ReflagContext
            {
                Company = new ReflagCompanyContext
                {
                    Id = "company-123",
                },
            },
            new ReflagTelemetryOptions
            {
                EnableTelemetry = false,
            });

        await client.DisposeAsync();

        AssertDisposed(() => bound.GetFlag("missing-flag"));
        AssertDisposed(() => bound.GetFlagsForBootstrap());
        await AssertDisposedAsync(() => bound.TrackAsync("checkout"));
        await AssertDisposedAsync(() => bound.FlushAsync());
        await AssertDisposedAsync(() => bound.RefreshFlagsAsync());
        AssertDisposed(() => bound.BindClient(new ReflagContext()));
        AssertDisposed(() => bound.BindClient(new { User = new { Id = "user-456" } }));
    }

    [Fact]
    public async Task InitializeAsync_fetches_flags_and_evaluates_locally()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(JsonSerializer.Serialize(
            new FeaturesEnvelope
            {
                Success = true,
                FlagStateVersion = 1,
                Features =
                [
                    TestDefinitions.CreateFlag(
                        "new-dashboard",
                        7,
                        new FlagContextFilterDefinition
                        {
                            Field = "company.id",
                            Operator = FlagContextFilterOperator.Is,
                            Values = ["company-123"],
                        }),
                ],
            },
            ReflagJson.Options));

        var logger = new TestLogger();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/path"),
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            Logger = logger,
        });

        await client.InitializeAsync();

        var enabled = client.GetFlag(
            "new-dashboard",
            new ReflagContext
            {
                Company = new ReflagCompanyContext { Id = "company-123" },
            });

        var bootstrapped = client.GetFlagsForBootstrap(
            new ReflagContext
            {
                Company = new ReflagCompanyContext { Id = "company-123" },
            });

        Assert.True(enabled);
        Assert.True(bootstrapped.Flags["new-dashboard"].Value);
        Assert.Equal(1, bootstrapped.FlagStateVersion);
        Assert.Equal("https://api.example.com/path/features", transport.GetCalls.Single().Url.ToString());
        Assert.Equal("dotnet-sdk/0.0.1", transport.GetCalls.Single().Headers["reflag-sdk-version"]); // x-release-please-version
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("Reflag initialized in"));
    }

    [Fact]
    public void GetFlag_before_initialize_logs_and_uses_overrides()
    {
        var logger = new TestLogger();
        var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            Logger = logger,
            FlagOverrides = new Dictionary<string, bool>
            {
                ["forced-flag"] = true,
            },
        });

        var enabled = client.GetFlag("forced-flag", new ReflagContext());
        var bootstrapped = client.GetFlagsForBootstrap(new ReflagContext());

        Assert.True(enabled);
        Assert.True(bootstrapped.Flags["forced-flag"].Value);
        Assert.Null(bootstrapped.FlagStateVersion);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("flag access: ReflagClient is not initialized yet."));
    }

    [Fact]
    public void BindClient_object_overload_normalizes_and_merges_context()
    {
        var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
        });

        var bound = client.BindClient(new
        {
            User = new
            {
                Id = "user-1",
                Name = "Ada",
                Plan = "pro",
            },
            Other = new
            {
                Region = "eu",
            },
        });

        var rebound = bound.BindClient(new
        {
            User = new
            {
                Email = "ada@example.com",
            },
            Other = new
            {
                Theme = "dark",
            },
        });

        Assert.Equal("user-1", rebound.User?.Id);
        Assert.Equal("Ada", rebound.User?.Name);
        Assert.Equal("ada@example.com", rebound.User?.Email);
        Assert.Equal("pro", rebound.User?.Attributes["Plan"]);
        Assert.Equal("eu", rebound.OtherContext?["Region"]);
        Assert.Equal("dark", rebound.OtherContext?["Theme"]);
    }

    [Fact]
    public async Task InitializeAsync_uses_static_fallback_when_fetch_fails()
    {
        var transport = new TestTransport();
        transport.EnqueueGetFailure(new HttpRequestException("boom"));
        var logger = new TestLogger();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            Logger = logger,
            FlagsFetchRetries = 0,
            FlagsFallbackProvider = ReflagFallbackProviders.Static(new Dictionary<string, bool>
            {
                ["fallback-flag"] = true,
            }),
        });

        await client.InitializeAsync();
        var enabled = client.GetFlag("fallback-flag", new ReflagContext());
        var bootstrapped = client.GetFlagsForBootstrap(new ReflagContext());
        var definitions = client.GetFlagDefinitions();

        Assert.True(enabled);
        Assert.True(bootstrapped.Flags["fallback-flag"].Value);
        Assert.Null(bootstrapped.FlagStateVersion);
        Assert.Single(definitions);
        Assert.Equal(0, definitions[0].Targeting.Version);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("remote flags unavailable, using fallback flags fetched"));
    }

    [Fact]
    public void BindClient_rejects_attribute_collisions_between_explicit_and_inferred_attributes()
    {
        var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
        });

        Assert.Throws<ArgumentException>(() =>
            client.BindClient(new
            {
                User = new
                {
                    Attributes = new { Plan = "free" },
                    plan = "pro",
                },
            }));
    }

    private static void AssertDisposed(Action action)
    {
        Assert.Throws<ObjectDisposedException>(action);
    }

    private static Task AssertDisposedAsync(Func<Task> action)
    {
        return Assert.ThrowsAsync<ObjectDisposedException>(action);
    }

    private sealed class FeaturesEnvelope
    {
        public bool Success { get; init; }

        public int FlagStateVersion { get; init; }

        public IReadOnlyList<FlagDefinition> Features { get; init; } = Array.Empty<FlagDefinition>();
    }
}
