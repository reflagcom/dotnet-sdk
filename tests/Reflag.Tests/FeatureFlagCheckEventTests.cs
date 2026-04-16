using System.Text.Json;
using Microsoft.Extensions.Logging;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class FeatureFlagCheckEventTests
{
    [Fact]
    public async Task GetFlag_followed_by_FlushAsync_sends_feature_flag_event_with_evaluation_metadata()
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
                            Values = ["company-456"],
                        }),
                ],
            },
            ReflagJson.Options));

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpTransport = transport,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();

        var enabled = client.GetFlag(
            "new-dashboard",
            new ReflagContext
            {
                User = new ReflagUserContext
                {
                    Id = "user-123",
                    Name = "Ada",
                    Attributes = new Dictionary<string, object?>
                    {
                        ["plan"] = "enterprise",
                    },
                },
                Company = new ReflagCompanyContext
                {
                    Id = "company-456",
                },
                Other = new Dictionary<string, object?>
                {
                    ["environment"] = "staging",
                },
            });

        await client.FlushAsync();

        Assert.True(enabled);
        Assert.Single(transport.PostCalls);
        var payload = ToJsonArray(transport.PostCalls[0].Body);
        Assert.Equal(3, payload.Count);

        var eventItem = payload.Single(item => item.GetProperty("type").GetString() == "feature-flag-event");
        Assert.Equal("check", eventItem.GetProperty("action").GetString());
        Assert.Equal("new-dashboard", eventItem.GetProperty("key").GetString());
        Assert.Equal(7, eventItem.GetProperty("targetingVersion").GetInt32());
        Assert.True(eventItem.GetProperty("evalResult").GetBoolean());
        Assert.Equal(new[] { true }, eventItem.GetProperty("evalRuleResults").EnumerateArray().Select(item => item.GetBoolean()).ToArray());
        Assert.Empty(eventItem.GetProperty("evalMissingFields").EnumerateArray());
        Assert.Equal("user-123", eventItem.GetProperty("evalContext").GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("Ada", eventItem.GetProperty("evalContext").GetProperty("user").GetProperty("name").GetString());
        Assert.Equal("enterprise", eventItem.GetProperty("evalContext").GetProperty("user").GetProperty("plan").GetString());
        Assert.Equal("company-456", eventItem.GetProperty("evalContext").GetProperty("company").GetProperty("id").GetString());
        Assert.Equal("staging", eventItem.GetProperty("evalContext").GetProperty("other").GetProperty("environment").GetString());
    }

    [Fact]
    public async Task GetFlag_for_unknown_flag_sends_feature_flag_event_without_targeting_metadata()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson("{\"success\":true,\"flagStateVersion\":1,\"features\":[]}");

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpTransport = transport,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        var enabled = client.GetFlag("unknown-flag", new ReflagContext());
        await client.FlushAsync();

        Assert.False(enabled);
        Assert.Single(transport.PostCalls);

        var payload = ToJsonArray(transport.PostCalls[0].Body);
        Assert.Single(payload);

        var eventItem = payload[0];
        Assert.Equal("feature-flag-event", eventItem.GetProperty("type").GetString());
        Assert.Equal("check", eventItem.GetProperty("action").GetString());
        Assert.Equal("unknown-flag", eventItem.GetProperty("key").GetString());
        Assert.False(eventItem.GetProperty("evalResult").GetBoolean());
        Assert.True(eventItem.TryGetProperty("evalContext", out var evalContext));
        Assert.Empty(evalContext.EnumerateObject());
        Assert.False(eventItem.TryGetProperty("targetingVersion", out _));
        Assert.False(eventItem.TryGetProperty("evalRuleResults", out _));
        Assert.False(eventItem.TryGetProperty("evalMissingFields", out _));
    }

    [Fact]
    public async Task GetFlag_with_telemetry_disabled_does_not_send_feature_flag_event()
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
                        new FlagConstantFilterDefinition
                        {
                            Value = true,
                        }),
                ],
            },
            ReflagJson.Options));

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpTransport = transport,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        var enabled = client.GetFlag(
            "new-dashboard",
            new ReflagContext
            {
                User = new ReflagUserContext { Id = "user-123" },
            },
            new ReflagTelemetryOptions
            {
                EnableTelemetry = false,
            });

        await client.FlushAsync();

        Assert.True(enabled);
        Assert.Empty(transport.PostCalls);
    }

    [Fact]
    public async Task GetFlag_dedupes_identical_check_events_within_window()
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
                        "demo-flag",
                        2,
                        new FlagConstantFilterDefinition
                        {
                            Value = true,
                        }),
                ],
            },
            ReflagJson.Options));

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpTransport = transport,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        Assert.True(client.GetFlag("demo-flag", new ReflagContext()));
        Assert.True(client.GetFlag("demo-flag", new ReflagContext()));
        await client.FlushAsync();

        Assert.Single(transport.PostCalls);
        var payload = ToJsonArray(transport.PostCalls[0].Body);
        Assert.Single(payload);
        Assert.Equal("feature-flag-event", payload[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task GetFlag_rate_limits_missing_context_field_warnings()
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
                        "requires-plan",
                        4,
                        new FlagContextFilterDefinition
                        {
                            Field = "company.plan",
                            Operator = FlagContextFilterOperator.Is,
                            Values = ["enterprise"],
                        }),
                ],
            },
            ReflagJson.Options));

        var logger = new TestLogger();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpTransport = transport,
            Logger = logger,
            FlagsFetchRetries = 0,
        });

        await client.InitializeAsync();
        Assert.False(client.GetFlag("requires-plan", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));
        Assert.False(client.GetFlag("requires-plan", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));

        var warningEntries = logger.Entries
            .Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("flag targeting rules might not be correctly evaluated due to missing context fields."))
            .ToList();

        Assert.Single(warningEntries);
    }

    private static List<JsonElement> ToJsonArray(object? body)
    {
        var json = JsonSerializer.Serialize(body, ReflagJson.Options);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
    }

    private sealed class FeaturesEnvelope
    {
        public bool Success { get; init; }

        public int FlagStateVersion { get; init; }

        public IReadOnlyList<FlagDefinition> Features { get; init; } = Array.Empty<FlagDefinition>();
    }
}
