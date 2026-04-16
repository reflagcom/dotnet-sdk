using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reflag;
using Reflag.MinimalApiExample;
using Xunit;

namespace Reflag.MinimalApiExample.Tests;

public sealed class ExampleAppTests
{
    [Fact]
    public async Task FlagsEndpointUsesTheExampleAppOfflineDemoOverrides()
    {
        await using var harness = await ExampleAppHarness.StartAsync();

        var demoFlag = await harness.GetFlagAsync("demo-flag");
        var newDashboard = await harness.GetFlagAsync("new-dashboard");

        Assert.True(demoFlag.Enabled);
        Assert.False(newDashboard.Enabled);
        Assert.Equal("Testing", demoFlag.Context.Other!["environment"]?.ToString());
    }

    [Fact]
    public async Task FlagsEndpointBindsTopLevelCompanyPlanAsACompanyAttribute()
    {
        await using var harness = await ExampleAppHarness.StartAsync();

        var response = await harness.GetFlagAsync("new-dashboard?context.user.id=user-123&context.company.id=company-456&context.company.plan=enterprise");

        Assert.Equal("company-456", response.Context.Company?.Id);
        Assert.Equal("enterprise", response.Context.Company?.Attributes["plan"]?.ToString());
    }

    [Fact]
    public async Task FlagsEndpointCanBeToggledInTestsWithPushFlagOverrides()
    {
        await using var harness = await ExampleAppHarness.StartAsync();
        var reflagClient = harness.App.Services.GetRequiredService<ReflagClient>();

        Assert.False((await harness.GetFlagAsync("new-dashboard")).Enabled);

        using (reflagClient.PushFlagOverrides(new Dictionary<string, bool>
        {
            ["new-dashboard"] = true,
        }))
        {
            Assert.True((await harness.GetFlagAsync("new-dashboard")).Enabled);
        }

        Assert.False((await harness.GetFlagAsync("new-dashboard")).Enabled);
    }

    [Theory]
    [InlineData("new-dashboard", true)]
    [InlineData("demo-flag", false)]
    public async Task BootstrapEndpointReflectsScopedOverrideValues(string key, bool expected)
    {
        await using var harness = await ExampleAppHarness.StartAsync();
        var reflagClient = harness.App.Services.GetRequiredService<ReflagClient>();

        using var _ = reflagClient.PushFlagOverrides(new Dictionary<string, bool>
        {
            [key] = expected,
        });

        var bootstrapped = await harness.HttpClient.GetFromJsonAsync<ReflagBootstrappedFlags>("/bootstrap?context.user.id=test-user");

        Assert.NotNull(bootstrapped);
        Assert.Equal(expected, bootstrapped.Flags[key].Value);
    }

    private sealed class ExampleAppHarness(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public HttpClient HttpClient { get; } = app.GetTestClient();

        public static async Task<ExampleAppHarness> StartAsync()
        {
            var app = ReflagMinimalApiExampleApp.BuildApp([], builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Environment.EnvironmentName = "Testing";
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["REFLAG_SECRET_KEY"] = string.Empty,
                    ["REFLAG_API_BASE_URL"] = string.Empty,
                    ["REFLAG_FALLBACK_AZURE_BLOB_CONTAINER"] = string.Empty,
                    ["REFLAG_FALLBACK_AZURE_BLOB_PREFIX"] = string.Empty,
                    ["AZURE_STORAGE_CONNECTION_STRING"] = string.Empty,
                });
            });

            await app.StartAsync();
            return new ExampleAppHarness(app);
        }

        public async Task<FlagEndpointResponse> GetFlagAsync(string keyOrPath)
        {
            var path = keyOrPath.StartsWith('/')
                ? keyOrPath
                : $"/flags/{keyOrPath}";
            var response = await HttpClient.GetFromJsonAsync<FlagEndpointResponse>(path);
            return Assert.IsType<FlagEndpointResponse>(response);
        }

        public ValueTask DisposeAsync()
        {
            return App.DisposeAsync();
        }
    }

    private sealed class FlagEndpointResponse
    {
        public string Key { get; init; } = string.Empty;

        public bool Enabled { get; init; }

        public ReflagContext Context { get; init; } = new();
    }
}
