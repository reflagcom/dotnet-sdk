using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class MultivariateClientTests
{
    private const string SecretKey = "validSecretKeyWithMoreThan22Chars";

    private sealed record ModelConfig(string Name, int MaxTokens);

    private static FlagDefinition ModelFlag() => TestDefinitions.CreateMultivariateFlag(
        "model",
        "quality",
        new Dictionary<string, object?>
        {
            ["quality"] = new { name = "large", maxTokens = 8000 },
            ["cheap"] = new { name = "small", maxTokens = 1000 },
            ["none"] = null,
        },
        new FlagContextFilterDefinition
        {
            Field = "company.plan",
            Operator = FlagContextFilterOperator.Is,
            Values = ["enterprise"],
        },
        defaultVariantKey: "cheap");

    private static FlagDefinition DependentFlag() => TestDefinitions.CreateFlag(
        "quality-banner",
        1,
        new FlagReferenceFilterDefinition { Key = "model", VariantKey = "quality" });

    private static readonly ReflagContext Enterprise = new()
    {
        Company = new ReflagCompanyContext
        {
            Id = "company-1",
            Attributes = new Dictionary<string, object?> { ["plan"] = "enterprise" },
        },
    };

    [Fact]
    public async Task GetFlag_reads_typed_and_arbitrary_json_values()
    {
        await using var client = await StartAsync(TestDefinitions.FlagsResponse(5, ModelFlag(), DependentFlag()));

        Assert.Equal(new ModelConfig("large", 8000), client.GetFlag("model", Enterprise, new ModelConfig("fallback", 0)));
        Assert.Equal("large", client.GetFlag<JsonNode?>("model", Enterprise, null)!["name"]!.GetValue<string>());
        Assert.Equal("small", client.GetFlag<JsonElement>("model", new ReflagContext(), default).GetProperty("name").GetString());
        Assert.Equal(8000, client.BindClient(Enterprise).GetFlag("model", new ModelConfig("fallback", 0)).MaxTokens);
        Assert.True(client.GetFlag("quality-banner", Enterprise));
        Assert.False(client.GetFlag("quality-banner", new ReflagContext()));
    }

    [Fact]
    public async Task GetFlag_uses_the_default_for_json_null_unless_reading_arbitrary_json()
    {
        var flag = TestDefinitions.CreateMultivariateFlag("nullable", "none", new Dictionary<string, object?> { ["none"] = null });
        await using var client = await StartAsync(TestDefinitions.FlagsResponse(1, flag));

        Assert.Equal("fallback", client.GetFlag("nullable", new ReflagContext(), "fallback"));
        Assert.Equal(JsonValueKind.Null, client.GetFlag<JsonElement?>("nullable", new ReflagContext(), null)!.Value.ValueKind);
        Assert.Null(client.GetFlag<JsonNode?>("nullable", new ReflagContext(), JsonValue.Create("fallback")));
    }

    [Fact]
    public async Task Type_mismatches_use_the_default_warn_and_report_no_selection()
    {
        var logger = new TestLogger();
        var transport = new TestTransport();
        transport.EnqueueGetJson(TestDefinitions.FlagsResponse(5, ModelFlag()));
        await using var client = new ReflagClient(Options(transport, logger));
        await client.InitializeAsync();

        Assert.False(client.GetFlag("model", Enterprise));
        Assert.Equal(7, client.GetFlag("model", Enterprise, 7));
        await client.FlushAsync();

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message == "flag \"model\" returned object; expected Boolean. Using the default value.");
        var check = ChecksFor(transport, "model").First();
        Assert.False(check.TryGetProperty("evalResult", out _));
        Assert.False(check.TryGetProperty("sourceVersionId", out _));
        Assert.Equal(new[] { true }, check.GetProperty("evalRuleResults").EnumerateArray().Select(item => item.GetBoolean()));
    }

    [Fact]
    public async Task GetFlags_returns_all_values_and_registers_a_check_per_flag()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(TestDefinitions.FlagsResponse(5, ModelFlag(), DependentFlag()));
        await using var client = new ReflagClient(Options(transport, new TestLogger()));
        await client.InitializeAsync();

        var values = client.BindClient(Enterprise).GetFlags();
        await client.FlushAsync();

        Assert.Equal(new[] { "model", "quality-banner" }, values.Keys);
        Assert.Equal("large", values["model"]!.Value.GetProperty("name").GetString());
        Assert.True(values["quality-banner"]!.Value.GetBoolean());
        Assert.Equal("quality", Assert.Single(ChecksFor(transport, "model")).GetProperty("evalResult").GetString());
        Assert.Equal("on", Assert.Single(ChecksFor(transport, "quality-banner")).GetProperty("evalResult").GetString());
    }

    [Fact]
    public async Task Json_overrides_apply_after_evaluation_without_changing_dependent_flags()
    {
        await using var client = await StartAsync(TestDefinitions.FlagsResponse(5, ModelFlag(), DependentFlag()));

        using (client.PushFlagOverrides(new Dictionary<string, object?> { ["model"] = new { name = "override", maxTokens = 1 } }))
        {
            Assert.Equal(new ModelConfig("override", 1), client.GetFlag("model", Enterprise, new ModelConfig("fallback", 0)));
            // The dependent flag still sees the evaluated "quality" selection.
            Assert.True(client.GetFlag("quality-banner", Enterprise));
            Assert.False(client.GetFlagsForBootstrap(Enterprise).Flags["model"].Resolved);
        }

        client.SetFlagOverrides(_ => new Dictionary<string, object?> { ["model"] = "plain" });
        Assert.Equal("plain", client.GetFlag("model", Enterprise, "fallback"));
        client.ClearFlagOverrides();
        Assert.Equal("large", client.GetFlag("model", Enterprise, new ModelConfig("fallback", 0)).Name);
    }

    [Fact]
    public async Task Accepted_collections_are_saved_as_v2_snapshots_and_restore_selections()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"reflag-tests-{Guid.NewGuid():N}");
        try
        {
            var segment = new SegmentDefinition
            {
                Key = "enterprise",
                Filter = new FlagContextFilterDefinition
                {
                    Field = "company.plan",
                    Operator = FlagContextFilterOperator.Is,
                    Values = ["enterprise"],
                },
            };
            var segmentFlag = TestDefinitions.CreateFlag("enterprise-only", 3, new SegmentReferenceFilterDefinition { Key = "enterprise" });
            var transport = new TestTransport();
            transport.EnqueueGetJson(TestDefinitions.FlagsResponse(9, [ModelFlag(), segmentFlag], [segment]));
            await using (var client = new ReflagClient(Options(transport, new TestLogger(), ReflagFallbackProviders.File(new FileFallbackProviderOptions { Directory = directory }))))
            {
                await client.InitializeAsync();
                await client.FlushAsync();
            }

            var file = Assert.Single(Directory.GetFiles(directory));
            Assert.StartsWith("flags-fallback-v2-", Path.GetFileName(file));
            using (var saved = JsonDocument.Parse(File.ReadAllText(file)))
            {
                Assert.Equal(2, saved.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(9, saved.RootElement.GetProperty("flagStateVersion").GetInt32());
                Assert.Equal(2, saved.RootElement.GetProperty("flags").GetArrayLength());
                Assert.Equal("enterprise", saved.RootElement.GetProperty("segments")[0].GetProperty("key").GetString());
            }

            var failing = new TestTransport();
            failing.EnqueueGetFailure(new HttpRequestException("offline"));
            var logger = new TestLogger();
            await using var restored = new ReflagClient(Options(failing, logger, ReflagFallbackProviders.File(new FileFallbackProviderOptions { Directory = directory })));
            await restored.InitializeAsync();

            var bootstrap = restored.GetFlagsForBootstrap(Enterprise);
            Assert.Equal(9, bootstrap.FlagStateVersion);
            Assert.True(bootstrap.Flags["enterprise-only"].Resolved);
            Assert.True(bootstrap.Flags["enterprise-only"].Value!.Value.GetBoolean());
            Assert.Equal("quality", bootstrap.Flags["model"].VariantKey);
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("using fallback flags", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Schema_version_1_snapshots_are_ignored()
    {
        var transport = new TestTransport();
        transport.EnqueueGetFailure(new HttpRequestException("offline"));
        var logger = new TestLogger();
        await using var client = new ReflagClient(Options(transport, logger, new FixedFallbackProvider(new FlagsFallbackSnapshot
        {
            SchemaVersion = 1,
            SavedAt = DateTimeOffset.UtcNow,
            Flags = [TestDefinitions.CreateFlag("legacy", 1, new FlagConstantFilterDefinition { Value = true })],
        })));

        await client.InitializeAsync();

        Assert.Empty(client.GetFlagDefinitions().Flags);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("invalid or unsupported snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Static_fallback_accepts_json_values_without_reporting_selections()
    {
        var transport = new TestTransport();
        transport.EnqueueGetFailure(new HttpRequestException("offline"));
        await using var client = new ReflagClient(Options(transport, new TestLogger(), ReflagFallbackProviders.Static(new Dictionary<string, object?>
        {
            ["model"] = new { name = "static", maxTokens = 10 },
            ["enabled"] = true,
        })));
        await client.InitializeAsync();

        Assert.Equal(new ModelConfig("static", 10), client.GetFlag("model", new ReflagContext(), new ModelConfig("fallback", 0)));
        Assert.True(client.GetFlag("enabled", new ReflagContext()));
        await client.FlushAsync();

        var check = Assert.Single(ChecksFor(transport, "model"));
        Assert.False(check.TryGetProperty("evalResult", out _));
        Assert.False(check.TryGetProperty("sourceVersionId", out _));
    }

    [Fact]
    public async Task Unsupported_protocol_versions_are_rejected()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson("{\"success\":true,\"protocolVersion\":3,\"flagStateVersion\":1,\"flags\":[],\"segments\":[]}");
        await using var client = new ReflagClient(Options(transport, new TestLogger()));
        await client.InitializeAsync();

        Assert.False(client.GetFlag("anything", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));
        Assert.Null(client.GetFlagsForBootstrap(new ReflagContext()).FlagStateVersion);
    }

    private static async Task<ReflagClient> StartAsync(string flagsResponse)
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(flagsResponse);
        var client = new ReflagClient(Options(transport, new TestLogger()));
        await client.InitializeAsync();
        return client;
    }

    private static ReflagClientOptions Options(TestTransport transport, TestLogger logger, IFlagsFallbackProvider? fallbackProvider = null) => new()
    {
        SecretKey = SecretKey,
        HttpClient = transport.CreateHttpClient(),
        FlagsSyncMode = ReflagFlagsSyncMode.Polling,
        FlagsFetchRetries = 0,
        Logger = logger,
        FlagsFallbackProvider = fallbackProvider,
    };

    private static List<JsonElement> ChecksFor(TestTransport transport, string key)
    {
        return transport.PostCalls
            .SelectMany(call =>
            {
                using var document = JsonDocument.Parse(call.Body);
                return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
            })
            .Where(item => item.GetProperty("type").GetString() == "feature-flag-event" && item.GetProperty("key").GetString() == key)
            .ToList();
    }

    private sealed class FixedFallbackProvider(FlagsFallbackSnapshot snapshot) : IFlagsFallbackProvider
    {
        public Task<FlagsFallbackSnapshot?> LoadAsync(FlagsFallbackProviderContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<FlagsFallbackSnapshot?>(snapshot);

        public Task SaveAsync(FlagsFallbackProviderContext context, FlagsFallbackSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
