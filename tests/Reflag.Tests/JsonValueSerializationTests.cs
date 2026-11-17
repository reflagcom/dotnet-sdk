using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class JsonValueSerializationTests
{
    [Theory]
    [InlineData("startup")]
    [InlineData("startup-factory")]
    [InlineData("runtime")]
    [InlineData("runtime-factory")]
    [InlineData("temporary")]
    [InlineData("temporary-factory")]
    [InlineData("static")]
    public async Task Json_value_sources_preserve_null_properties(string source)
    {
        using var document = JsonDocument.Parse("{\"name\":null}");
        var values = new Dictionary<string, object?>
        {
            ["poco"] = new NullableSettings { Name = null },
            ["anonymous"] = new
            {
                Name = (string?)null,
                Nested = new { Label = (string?)null },
                Items = new[] { new { Label = (string?)null } },
            },
            ["dictionary"] = new Dictionary<string, object?> { ["name"] = null },
            ["element"] = document.RootElement,
            ["none"] = null,
        };
        var transport = new TestTransport();
        transport.EnqueueGetFailure(new HttpRequestException("offline"));
        using var httpClient = transport.CreateHttpClient();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            HttpClient = httpClient,
            Offline = source != "static",
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            FlagsFetchRetries = 0,
            JsonFlagOverrides = source == "startup" ? values : null,
            JsonFlagOverridesFactory = source == "startup-factory" ? _ => values : null,
            FlagsFallbackProvider = source == "static" ? ReflagFallbackProviders.Static(values) : null,
        });
        await client.InitializeAsync();

        if (source == "runtime")
        {
            client.SetFlagOverrides(values);
        }
        else if (source == "runtime-factory")
        {
            client.SetFlagOverrides(_ => values);
        }

        using var layer = source switch
        {
            "temporary" => client.PushFlagOverrides(values),
            "temporary-factory" => client.PushFlagOverrides(_ => values),
            _ => null,
        };
        var context = new ReflagContext();
        var telemetry = new ReflagTelemetryOptions { EnableTelemetry = false };

        foreach (var key in new[] { "poco", "anonymous", "dictionary", "element" })
        {
            var value = client.GetFlag<JsonElement>(key, context, default, telemetry);
            Assert.Equal(JsonValueKind.Null, value.GetProperty("name").ValueKind);
        }

        var typed = client.GetFlag("poco", context, new NullableSettings { Name = "fallback" }, telemetry);
        Assert.Null(typed.Name);
        var anonymous = client.GetFlag<JsonElement>("anonymous", context, default, telemetry);
        Assert.Equal(JsonValueKind.Null, anonymous.GetProperty("nested").GetProperty("label").ValueKind);
        Assert.Equal(JsonValueKind.Null, anonymous.GetProperty("items")[0].GetProperty("label").ValueKind);
        Assert.Equal(JsonValueKind.Null, client.GetFlag<JsonElement>("none", context, default, telemetry).ValueKind);

        using var bootstrap = JsonDocument.Parse(JsonSerializer.Serialize(client.GetFlagsForBootstrap(context, telemetry)));
        var flag = bootstrap.RootElement.GetProperty("flags").GetProperty("poco");
        Assert.Equal(JsonValueKind.Null, flag.GetProperty("value").GetProperty("name").ValueKind);
        Assert.False(flag.GetProperty("resolved").GetBoolean());
        Assert.False(flag.TryGetProperty("variantKey", out _));
        Assert.False(flag.TryGetProperty("sourceVersionId", out _));
        Assert.False(flag.TryGetProperty("matchedRuleId", out _));
        Assert.False(bootstrap.RootElement.TryGetProperty("flagStateVersion", out _));
    }

    [Fact]
    public void Transport_serialization_still_omits_null_properties()
    {
        Assert.Equal("{}", JsonSerializer.Serialize(new { Name = (string?)null }, ReflagJson.Options));
    }

    private sealed class NullableSettings
    {
        public string? Name { get; init; } = "initializer";
    }
}
