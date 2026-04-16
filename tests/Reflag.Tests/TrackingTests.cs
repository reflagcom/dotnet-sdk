using System.Text.Json;
using Microsoft.Extensions.Logging;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class TrackingTests
{
    [Fact]
    public async Task BindClient_followed_by_FlushAsync_sends_company_and_user_bulk_items()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson("{\"success\":true,\"flagStateVersion\":1,\"features\":[]}");
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
        _ = client.BindClient(new ReflagContext
        {
            User = new ReflagUserContext
            {
                Id = "user-123",
                Name = "Ada",
                Email = "ada@example.com",
            },
            Company = new ReflagCompanyContext
            {
                Id = "company-456",
                Name = "Acme",
                Attributes = new Dictionary<string, object?>
                {
                    ["plan"] = "enterprise",
                },
            },
        }, new ReflagTelemetryOptions
        {
            Active = true,
        });

        await client.FlushAsync();

        Assert.Single(transport.PostCalls);
        var payload = ToJsonArray(transport.PostCalls[0].Body);
        Assert.Equal("https://api.example.com/bulk", transport.PostCalls[0].Url.ToString());

        var userItem = payload.Single(item => item.GetProperty("type").GetString() == "user");
        Assert.Equal("user-123", userItem.GetProperty("userId").GetString());
        Assert.Equal("Ada", userItem.GetProperty("attributes").GetProperty("name").GetString());
        Assert.Equal("ada@example.com", userItem.GetProperty("attributes").GetProperty("email").GetString());
        Assert.True(userItem.GetProperty("context").GetProperty("active").GetBoolean());

        var companyItem = payload.Single(item => item.GetProperty("type").GetString() == "company");
        Assert.Equal("company-456", companyItem.GetProperty("companyId").GetString());
        Assert.Equal("user-123", companyItem.GetProperty("userId").GetString());
        Assert.Equal("Acme", companyItem.GetProperty("attributes").GetProperty("name").GetString());
        Assert.Equal("enterprise", companyItem.GetProperty("attributes").GetProperty("plan").GetString());
        Assert.True(companyItem.GetProperty("context").GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task GetFlagsForBootstrap_followed_by_FlushAsync_sends_context_sync_events()
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
        _ = client.GetFlagsForBootstrap(new ReflagContext
        {
            User = new ReflagUserContext
            {
                Id = "user-123",
            },
            Company = new ReflagCompanyContext
            {
                Id = "company-456",
            },
        });

        await client.FlushAsync();

        Assert.Single(transport.PostCalls);
        var payload = ToJsonArray(transport.PostCalls[0].Body);
        Assert.Equal(2, payload.Count);
        Assert.Contains(payload, item => item.GetProperty("type").GetString() == "user");
        Assert.Contains(payload, item => item.GetProperty("type").GetString() == "company");
    }

    [Fact]
    public async Task UpdateUserAsync_dedupes_identical_updates_within_window()
    {
        var transport = new TestTransport();

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpTransport = transport,
        });

        await client.UpdateUserAsync("user-123", new ReflagTrackOptions
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Ada",
            },
            Active = true,
        });

        await client.UpdateUserAsync("user-123", new ReflagTrackOptions
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Ada",
            },
            Active = true,
        });

        await client.FlushAsync();

        Assert.Single(transport.PostCalls);
        var payload = ToJsonArray(transport.PostCalls[0].Body);
        Assert.Single(payload);
        Assert.Equal("user", payload[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task BoundClient_TrackAsync_uses_bound_user_and_company()
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
        var bound = client.BindClient(new ReflagContext
        {
            User = new ReflagUserContext { Id = "user-123" },
            Company = new ReflagCompanyContext { Id = "company-456" },
        });

        await bound.TrackAsync("checkout-started", new ReflagEventTrackOptions
        {
            Attributes = new Dictionary<string, object?>
            {
                ["cartValue"] = 199,
            },
            Active = true,
        });

        await client.FlushAsync();

        Assert.Single(transport.PostCalls);
        var payload = ToJsonArray(transport.PostCalls[0].Body);
        Assert.Equal(3, payload.Count);
        var eventItem = payload.Single(item => item.GetProperty("type").GetString() == "event");
        Assert.Equal("checkout-started", eventItem.GetProperty("event").GetString());
        Assert.Equal("user-123", eventItem.GetProperty("userId").GetString());
        Assert.Equal("company-456", eventItem.GetProperty("companyId").GetString());
        Assert.Equal(199, eventItem.GetProperty("attributes").GetProperty("cartValue").GetInt32());
        Assert.True(eventItem.GetProperty("context").GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task BoundClient_TrackAsync_without_user_logs_warning_and_does_not_send_event()
    {
        var transport = new TestTransport();
        var logger = new TestLogger();

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpTransport = transport,
            Logger = logger,
        });

        var bound = client.BindClient(new ReflagContext
        {
            Company = new ReflagCompanyContext { Id = "company-456" },
        });

        await bound.TrackAsync("checkout-started");
        await client.FlushAsync();

        Assert.Single(transport.PostCalls);
        var payload = ToJsonArray(transport.PostCalls[0].Body);
        Assert.Single(payload);
        Assert.Equal("company", payload[0].GetProperty("type").GetString());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("no user set, cannot track event"));
    }

    [Fact]
    public async Task Telemetry_disabled_prevents_context_sync_and_bound_tracking()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson("{\"success\":true,\"flagStateVersion\":1,\"features\":[]}");
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
        var bound = client.BindClient(new ReflagContext
        {
            User = new ReflagUserContext { Id = "user-123" },
            Company = new ReflagCompanyContext { Id = "company-456" },
        }, new ReflagTelemetryOptions
        {
            EnableTelemetry = false,
            Active = true,
        });

        _ = bound.GetFlagsForBootstrap();
        await bound.TrackAsync("checkout-started");
        await client.FlushAsync();

        Assert.Empty(transport.PostCalls);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("telemetry disabled, not updating user/company"));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("telemetry disabled for this bound client, not tracking event"));
    }

    [Fact]
    public async Task FlushAsync_discards_items_when_bulk_send_fails_without_throwing()
    {
        var transport = new TestTransport();
        transport.EnqueuePostFailure(new HttpRequestException("boom"));
        var logger = new TestLogger();

        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            ApiBaseUrl = new Uri("https://api.example.com/"),
            HttpTransport = transport,
            Logger = logger,
        });

        await client.UpdateUserAsync("user-123");
        await client.FlushAsync();

        Assert.Single(transport.PostCalls);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("flush of buffered items failed; discarding items"));
    }

    private static List<JsonElement> ToJsonArray(object? body)
    {
        var json = JsonSerializer.Serialize(body, ReflagJson.Options);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToList();
    }
}
