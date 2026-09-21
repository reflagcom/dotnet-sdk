using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class RemoteConfigTests
{
    // Config has its own version and ordered variants.
    private const string Definition = """
        {
          "key": "experiment",
          "targeting": { "version": 7, "rules": [] },
          "config": {
            "version": 42,
            "variants": [
              {
                "key": "treatment",
                "payload": { "label": "Try it", "limits": [1, 2], "nested": { "enabled": true } },
                "filter": { "type": "context", "field": "company.id", "operator": "IS", "values": ["acme"] }
              },
              { "key": "control", "payload": null, "filter": { "type": "constant", "value": true } }
            ]
          }
        }
        """;

    [Theory]
    [InlineData("acme", "treatment")]
    [InlineData("other", "control")]
    public async Task Selects_first_matching_variant_independently_of_access(string companyId, string expected)
    {
        var transport = CreateTransport();
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var context = ReflagContext.From(new { Company = new { Id = companyId } });
        var bound = client.BindClient(context);

        Assert.False(bound.GetFlag("experiment"));
        var config = bound.GetFlagConfig("experiment");
        Assert.Equal(expected, config.Key);
        Assert.Equal(expected, client.GetFlagConfig("experiment", context).Key);
        if (expected == "treatment")
        {
            Assert.Equal("Try it", config.Payload.GetProperty("label").GetString());
            Assert.True(config.Payload.GetProperty("nested").GetProperty("enabled").GetBoolean());
        }

        var bootstrap = bound.GetFlagsForBootstrap();
        var raw = bootstrap.Flags["experiment"];
        Assert.Equal(7, raw.TargetingVersion);
        Assert.Equal(42, raw.Config!.TargetingVersion);
        Assert.Equal(new[] { companyId == "acme", true }, raw.Config.RuleEvaluationResults);
        Assert.Equal(1, bootstrap.FlagStateVersion);
        Assert.Equal(2, Assert.Single(client.GetFlagDefinitions()).Config!.Variants.Count);
        Assert.Single(transport.GetCalls);

        await client.FlushAsync();
        var events = Events(transport);
        Assert.Equal(2, events.Length);
        var check = Assert.Single(events, item => item.GetProperty("action").GetString() == "check");
        Assert.Equal(7, check.GetProperty("targetingVersion").GetInt32());
        Assert.False(check.GetProperty("evalResult").GetBoolean());
        var configCheck = Assert.Single(events, item => item.GetProperty("action").GetString() == "check-config");
        Assert.Equal(42, configCheck.GetProperty("targetingVersion").GetInt32());
        Assert.Equal(expected, configCheck.GetProperty("evalResult").GetProperty("key").GetString());
        Assert.Equal(companyId, configCheck.GetProperty("evalContext").GetProperty("company").GetProperty("id").GetString());
        Assert.False(configCheck.GetProperty("evalResult").TryGetProperty("targetingVersion", out _));
        Assert.False(configCheck.TryGetProperty("evalErrors", out _));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("absent")]
    [InlineData("empty")]
    [InlineData("unmatched")]
    public async Task Missing_configs_return_an_empty_value_and_serialize_safely(string scenario)
    {
        var definition = scenario switch
        {
            "unknown" => "",
            "absent" => """{"key":"experiment","targeting":{"version":1,"rules":[]}}""",
            "empty" => """{"key":"experiment","targeting":{"version":1,"rules":[]},"config":{"version":9,"variants":[]}}""",
            _ => SingleVariant("false", "\"unused\""),
        };
        var transport = CreateTransport(definition);
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var config = client.GetFlagConfig("experiment", new ReflagContext());
        Assert.Null(config.Key);
        Assert.Equal(JsonValueKind.Undefined, config.Payload.ValueKind);
        Assert.Equal("{}", JsonSerializer.Serialize(config));
        await client.FlushAsync();
        var item = Assert.Single(Events(transport));
        Assert.Equal("check-config", item.GetProperty("action").GetString());
        Assert.Empty(item.GetProperty("evalResult").EnumerateObject());
        Assert.Equal(scenario is "empty" or "unmatched", item.TryGetProperty("targetingVersion", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("123.45")]
    [InlineData("\"hello\"")]
    [InlineData("[1,\"two\",null]")]
    [InlineData("{\"nested\":{\"value\":true}}")]
    public async Task Preserves_all_JSON_payload_types_in_definitions_bootstrap_and_events(string? payload)
    {
        var transport = CreateTransport(SingleVariant("true", payload));
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var config = client.GetFlagConfig("experiment", new ReflagContext());
        Assert.Equal("variant", config.Key);
        AssertPayload(JsonSerializer.SerializeToElement(config), payload);

        var definitionJson = JsonSerializer.SerializeToElement(Assert.Single(client.GetFlagDefinitions()));
        AssertPayload(definitionJson.GetProperty("config").GetProperty("variants")[0], payload);
        var bootstrapJson = JsonSerializer.SerializeToElement(client.GetFlagsForBootstrap(new ReflagContext()), ReflagJson.Options);
        AssertPayload(bootstrapJson.GetProperty("flags").GetProperty("experiment").GetProperty("config"), payload);
        await client.FlushAsync();
        AssertPayload(Assert.Single(Events(transport)).GetProperty("evalResult"), payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Config_diagnostics_are_separate_and_rate_limited(bool unsupportedArray)
    {
        var definition = Definition.Replace("company.id", "other.plan").Replace("\"IS\"", "\"GT\"");
        var transport = CreateTransport(definition);
        var logger = new TestLogger();
        await using var client = CreateClient(transport, logger: logger);
        await client.InitializeAsync();
        var context = new ReflagContext
        {
            Other = unsupportedArray ? new Dictionary<string, object?> { ["plan"] = new[] { "enterprise" } } : null,
        };
        Assert.Equal("control", client.GetFlagConfig("experiment", context).Key);
        Assert.Equal("control", client.GetFlagConfig("experiment", context).Key);
        var bootstrap = client.GetFlagsForBootstrap(context).Flags["experiment"];
        Assert.Null(bootstrap.Errors);
        var error = Assert.Single(bootstrap.Config!.Errors!);
        Assert.Equal(unsupportedArray ? "UNSUPPORTED_ARRAY_OPERATOR" : "MISSING_CONTEXT_FIELD", error.Code);
        Assert.Equal("other.plan", error.Field);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("targeting rules"));

        await client.FlushAsync();
        var item = Assert.Single(Events(transport));
        Assert.Equal(error.Code, item.GetProperty("evalErrors")[0].GetProperty("code").GetString());
        Assert.Equal(new[] { false, true }, item.GetProperty("evalRuleResults").EnumerateArray().Select(value => value.GetBoolean()));
        Assert.Equal(unsupportedArray ? 0 : 1, item.GetProperty("evalMissingFields").GetArrayLength());
    }

    [Fact]
    public async Task Bootstrap_and_boolean_reads_do_not_record_config_exposure()
    {
        var transport = CreateTransport();
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        client.GetFlagsForBootstrap(new ReflagContext());
        await client.FlushAsync();
        Assert.Empty(transport.PostCalls);
        client.GetFlag("experiment", new ReflagContext());
        await client.FlushAsync();
        Assert.Equal("check", Assert.Single(Events(transport)).GetProperty("action").GetString());
    }

    [Fact]
    public async Task Disabled_telemetry_suppresses_context_updates_and_config_checks()
    {
        var transport = CreateTransport();
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var bound = client.BindClient(new { User = new { Id = "user" }, Company = new { Id = "acme" } },
            new ReflagTelemetryOptions { EnableTelemetry = false });
        Assert.Equal("treatment", bound.GetFlagConfig("experiment").Key);
        await client.FlushAsync();
        Assert.Empty(transport.PostCalls);
    }

    [Fact]
    public async Task Refresh_replaces_config_and_uses_its_version_for_event_deduplication()
    {
        var transport = CreateTransport();
        transport.EnqueueGetJson(Envelope(Definition.Replace("\"version\": 42", "\"version\": 43"), 2));
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var context = ReflagContext.From(new { Company = new { Id = "acme" } });
        client.GetFlagConfig("experiment", context);
        await client.RefreshFlagsAsync(2);
        client.GetFlagConfig("experiment", context);
        await client.FlushAsync();
        Assert.Equal(new[] { 42, 43 }, Events(transport).Select(item => item.GetProperty("targetingVersion").GetInt32()));
        Assert.Equal(43, client.GetFlagsForBootstrap(context).Flags["experiment"].Config!.TargetingVersion);
    }

    [Theory]
    [InlineData("{\"version\":-1,\"variants\":[]}")]
    [InlineData("{\"version\":1,\"variants\":null}")]
    [InlineData("{\"version\":1,\"variants\":[null]}")]
    [InlineData("{\"version\":1,\"variants\":[{\"key\":\"broken\"}]}")]
    [InlineData("{\"version\":1,\"variants\":[{\"key\":\"\",\"filter\":{\"type\":\"constant\",\"value\":true}}]}")]
    public async Task Invalid_config_refresh_preserves_the_last_good_definitions(string config)
    {
        var transport = CreateTransport();
        transport.EnqueueGetJson(Envelope("{\"key\":\"experiment\",\"config\":" + config + "}", 2));
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        await client.RefreshFlagsAsync(2);
        var context = ReflagContext.From(new { Company = new { Id = "acme" } });
        Assert.Equal("treatment", client.GetFlagConfig("experiment", context).Key);
        Assert.Equal(42, Assert.Single(client.GetFlagDefinitions()).Config!.Version);
        Assert.Equal(1, client.GetFlagsForBootstrap(context).FlagStateVersion);
    }

    [Fact]
    public async Task Config_checks_distinguish_contexts_and_override_payloads()
    {
        var transport = CreateTransport();
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var acme = ReflagContext.From(new { Company = new { Id = "acme" } });
        var other = ReflagContext.From(new { Company = new { Id = "other" } });
        Assert.Equal("treatment", client.GetFlagConfig("experiment", acme).Key);
        Assert.Equal("control", client.GetFlagConfig("experiment", other).Key);
        for (var limit = 1; limit <= 2; limit++)
        {
            client.SetFlagOverrides(new Dictionary<string, ReflagFlagOverride>
            {
                ["experiment"] = new()
                {
                    Config = new ReflagFlagConfig { Key = "same-key", Payload = JsonSerializer.SerializeToElement(limit) },
                },
            });
            client.GetFlagConfig("experiment", acme);
            client.GetFlagConfig("experiment", acme);
        }

        await client.FlushAsync();
        var events = Events(transport);
        Assert.Equal(4, events.Length);
        var overrides = events.Skip(2).ToArray();
        Assert.Equal(new[] { 1, 2 }, overrides.Select(item => item.GetProperty("evalResult").GetProperty("payload").GetInt32()));
        Assert.All(overrides, item =>
        {
            Assert.False(item.TryGetProperty("targetingVersion", out _));
            Assert.False(item.TryGetProperty("evalRuleResults", out _));
            Assert.False(item.TryGetProperty("evalErrors", out _));
        });
    }

    [Fact]
    public async Task Saved_definitions_round_trip_through_file_fallback_and_evaluate_after_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "reflag-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = ReflagFallbackProviders.File(new FileFallbackProviderOptions { Directory = directory });
            var transport = CreateTransport();
            var savingProvider = new SavingFallbackProvider(provider);
            await using (var client = CreateClient(transport, savingProvider))
            {
                await client.InitializeAsync();
                await savingProvider.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            var failingTransport = new TestTransport();
            failingTransport.EnqueueGetFailure(new HttpRequestException("offline"));
            await using var restarted = CreateClient(failingTransport, provider);
            await restarted.InitializeAsync();
            var context = ReflagContext.From(new { Company = new { Id = "acme" } });
            Assert.Equal("treatment", restarted.GetFlagConfig("experiment", context).Key);
            Assert.Equal("Try it", restarted.GetFlagConfig("experiment", context).Payload.GetProperty("label").GetString());
            Assert.Null(restarted.GetFlagsForBootstrap(context).FlagStateVersion);
            Assert.Equal(42, Assert.Single(restarted.GetFlagDefinitions()).Config!.Version);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Config_overrides_support_offline_factories_scopes_and_boolean_replacement()
    {
        var transport = new TestTransport();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            HttpClient = transport.CreateHttpClient(),
            FlagOverridesWithConfigFactory = context => new Dictionary<string, ReflagFlagOverride>
            {
                ["experiment"] = Override(context.Company?.Id ?? "default"),
            },
        });
        await client.InitializeAsync();
        var context = ReflagContext.From(new { Company = new { Id = "acme" } });
        Assert.Equal("acme", client.GetFlagConfig("experiment", context).Key);
        using (client.PushFlagOverrides(new Dictionary<string, ReflagFlagOverride> { ["experiment"] = Override("scoped") }))
        {
            Assert.Equal("scoped", client.GetFlagConfig("experiment", context).Key);
            using (client.PushFlagOverrides(new Dictionary<string, bool> { ["experiment"] = true }))
            {
                Assert.Null(client.GetFlagConfig("experiment", context).Key);
                Assert.True(client.GetFlag("experiment", context));
            }
            Assert.Equal("scoped", client.GetFlagConfig("experiment", context).Key);
        }
        Assert.Equal("acme", client.GetFlagConfig("experiment", context).Key);
        client.SetFlagOverrides(new Dictionary<string, ReflagFlagOverride> { ["experiment"] = Override("replacement") });
        var raw = client.GetFlagsForBootstrap(context).Flags["experiment"];
        Assert.Equal("replacement", raw.Config!.Key);
        Assert.Null(raw.Config.TargetingVersion);
        Assert.Null(raw.Config.RuleEvaluationResults);
        client.ClearFlagOverrides();
        Assert.Null(client.GetFlagConfig("experiment", context).Key);
        await client.FlushAsync();
        Assert.Empty(transport.GetCalls);
        Assert.Empty(transport.PostCalls);
    }

    [Fact]
    public async Task Config_access_validates_keys_context_and_disposal_and_is_safe_before_initialization()
    {
        var transport = CreateTransport();
        var client = CreateClient(transport);
        Assert.Null(client.GetFlagConfig("experiment", new ReflagContext()).Key);
        Assert.Throws<ArgumentException>(() => client.GetFlagConfig(" ", new ReflagContext()));
        Assert.Throws<ArgumentNullException>(() => client.GetFlagConfig("experiment", null!));
        await client.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => client.GetFlagConfig("experiment", new ReflagContext()));
    }

    [Fact]
    public async Task Constructor_config_overrides_and_runtime_factories_share_the_existing_override_layers()
    {
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            FlagOverridesWithConfig = new Dictionary<string, ReflagFlagOverride> { ["experiment"] = Override("initial") },
        });
        await client.InitializeAsync();
        var context = new ReflagContext();
        Assert.Equal("initial", client.GetFlagConfig("experiment", context).Key);
        client.SetFlagOverrides(_ => new Dictionary<string, ReflagFlagOverride> { ["experiment"] = Override("factory") });
        using (client.PushFlagOverrides(_ => new Dictionary<string, ReflagFlagOverride> { ["experiment"] = Override("layer") }))
        {
            client.ClearFlagOverrides();
            Assert.Equal("layer", client.GetFlagConfig("experiment", context).Key);
        }
        Assert.Null(client.GetFlagConfig("experiment", context).Key);
    }

    [Fact]
    public void Conflicting_override_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            FlagOverrides = new Dictionary<string, bool>(),
            FlagOverridesWithConfig = new Dictionary<string, ReflagFlagOverride>(),
        }));
        Assert.Throws<ArgumentException>(() => new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            FlagOverridesWithConfigFactory = _ => new Dictionary<string, ReflagFlagOverride>(),
            FlagOverridesWithConfig = new Dictionary<string, ReflagFlagOverride>(),
        }));
    }

    [Fact]
    public async Task Typed_config_deserializes_records_and_preserves_raw_telemetry()
    {
        var transport = CreateTransport();
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var context = ReflagContext.From(new { Company = new { Id = "acme" } });

        var config = client.GetFlagConfig<TestPayload>("experiment", context);
        Assert.Equal("treatment", config.Key);
        Assert.Equal("Try it", config.Payload!.Label);
        Assert.Equal(new[] { 1, 2 }, config.Payload.Limits);
        var boundConfig = client.BindClient(context).GetFlagConfig<TestPayload>("experiment");
        Assert.Equal(config.Payload.Label, boundConfig.Payload!.Label);
        client.GetFlagConfig("experiment", context);

        await client.FlushAsync();
        var item = Assert.Single(Events(transport));
        Assert.Equal("check-config", item.GetProperty("action").GetString());
        Assert.Equal(42, item.GetProperty("targetingVersion").GetInt32());
        // Telemetry keeps fields that the DTO omits.
        Assert.True(item.GetProperty("evalResult").GetProperty("payload").GetProperty("nested").GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task Typed_configs_handle_missing_and_null_payloads(string? payload)
    {
        var transport = CreateTransport(SingleVariant("true", payload));
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var context = new ReflagContext();
        var config = client.GetFlagConfig<TestPayload>("experiment", context);
        Assert.Equal("variant", config.Key);
        Assert.Null(config.Payload);
        Assert.Null(client.GetFlagConfig<int?>("experiment", context).Payload);
        if (payload is null)
        {
            Assert.Equal(0, client.GetFlagConfig<int>("experiment", context).Payload);
        }
        else
        {
            Assert.Throws<JsonException>(() => client.GetFlagConfig<int>("experiment", context));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"key\":\"experiment\"}")]
    [InlineData("{\"key\":\"experiment\",\"config\":{\"version\":1,\"variants\":[]}}")]
    public async Task Typed_configs_return_empty_results_when_no_config_is_selected(string definition)
    {
        var transport = CreateTransport(definition);
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var config = client.GetFlagConfig<TestPayload>("experiment", new ReflagContext());
        Assert.Null(config.Key);
        Assert.Null(config.Payload);
    }

    [Fact]
    public async Task Typed_configs_support_scalars_collections_and_offline_overrides()
    {
        await using var client = new ReflagClient(new ReflagClientOptions { Offline = true });
        await client.InitializeAsync();
        client.SetFlagOverrides(new Dictionary<string, ReflagFlagOverride>
        {
            ["number"] = new() { Config = new ReflagFlagConfig { Key = "scalar", Payload = JsonSerializer.SerializeToElement(42) } },
            ["array"] = new() { Config = new ReflagFlagConfig { Key = "list", Payload = JsonSerializer.SerializeToElement(new[] { "a", "b" }) } },
        });
        var bound = client.BindClient(new ReflagContext());
        var number = bound.GetFlagConfig<int>("number");
        Assert.Equal(42, number.Payload);
        Assert.Equal(42, JsonSerializer.SerializeToElement(number).GetProperty("payload").GetInt32());
        Assert.Equal(0, JsonSerializer.SerializeToElement(new ReflagFlagConfig<int> { Key = "zero", Payload = 0 }).GetProperty("payload").GetInt32());
        Assert.Equal(new[] { "a", "b" }, bound.GetFlagConfig<string[]>("array").Payload);
    }

    [Fact]
    public async Task Typed_configs_use_custom_options_and_inherit_bound_telemetry_settings()
    {
        var transport = CreateTransport(SingleVariant("true", "{\"label\":\"custom\",\"limits\":[3]}"));
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        var context = ReflagContext.From(new { User = new { Id = "user" } });
        var options = new JsonSerializerOptions();
        options.Converters.Add(new InterfacePayloadConverter());
        var disabled = new ReflagTelemetryOptions { EnableTelemetry = false };

        var config = client.GetFlagConfig<ITestPayload>("experiment", context, disabled, options);
        Assert.Equal("custom", config.Payload!.Label);
        var bound = client.BindClient(context, disabled);
        Assert.Equal("custom", bound.GetFlagConfig<ITestPayload>("experiment", serializerOptions: options).Payload!.Label);
        // Caller options replace the defaults.
        Assert.Null(bound.GetFlagConfig<TestPayload>("experiment", new JsonSerializerOptions()).Payload!.Label);
        Assert.Throws<NotSupportedException>(() => bound.GetFlagConfig<ITestPayload>("experiment"));
        await client.FlushAsync();
        Assert.Empty(transport.PostCalls);
    }

    [Fact]
    public async Task Typed_configs_propagate_incompatible_payload_errors_and_record_original_check()
    {
        var transport = CreateTransport(SingleVariant("true", "{\"label\":123}"));
        await using var client = CreateClient(transport);
        await client.InitializeAsync();
        Assert.Throws<JsonException>(() => client.GetFlagConfig<TestPayload>("experiment", new ReflagContext()));
        await client.FlushAsync();
        var item = Assert.Single(Events(transport));
        Assert.Equal(123, item.GetProperty("evalResult").GetProperty("payload").GetProperty("label").GetInt32());
    }

    private interface ITestPayload
    {
        string Label { get; }
    }

    private sealed record TestPayload(string Label, int[] Limits) : ITestPayload;

    private sealed class InterfacePayloadConverter : JsonConverter<ITestPayload>
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

        public override ITestPayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<TestPayload>(ref reader, Options);

        public override void Write(Utf8JsonWriter writer, ITestPayload value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }

    private static ReflagFlagOverride Override(string key) => new()
    {
        IsEnabled = false,
        Config = new ReflagFlagConfig { Key = key, Payload = JsonSerializer.SerializeToElement(new { limit = 5 }) },
    };

    private static string SingleVariant(string filterValue, string? payload) =>
        """{"key":"experiment","targeting":{"version":1,"rules":[]},"config":{"version":9,"variants":[{"key":"variant","filter":{"type":"constant","value":FILTER}PAYLOAD}]}}"""
            .Replace("FILTER", filterValue).Replace("PAYLOAD", payload is null ? "" : ",\"payload\":" + payload);

    private static string Envelope(string definition, int version = 1) =>
        "{\"success\":true,\"flagStateVersion\":" + version + ",\"features\":[" + definition + "]}";

    private static TestTransport CreateTransport(string definition = Definition)
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(Envelope(definition));
        return transport;
    }

    private static ReflagClient CreateClient(TestTransport transport, IFlagsFallbackProvider? provider = null, ILogger? logger = null) => new(new ReflagClientOptions
    {
        SecretKey = "validSecretKeyWithMoreThan22Chars",
        HttpClient = transport.CreateHttpClient(),
        FlagsSyncMode = ReflagFlagsSyncMode.Polling,
        FlagsFetchRetries = 0,
        FlagsFallbackProvider = provider,
        Logger = logger,
        Batch = new ReflagBatchOptions { Interval = TimeSpan.FromHours(1) },
    });

    private static JsonElement[] Events(TestTransport transport) => transport.PostCalls
        .SelectMany(call => JsonSerializer.Deserialize<JsonElement[]>(call.Body)!)
        .Where(item => item.GetProperty("type").GetString() == "feature-flag-event").ToArray();

    private static void AssertPayload(JsonElement element, string? expected)
    {
        Assert.Equal(expected is not null, element.TryGetProperty("payload", out var payload));
        if (expected is not null) Assert.Equal(expected, JsonSerializer.Serialize(payload));
    }

    private sealed class SavingFallbackProvider(IFlagsFallbackProvider inner) : IFlagsFallbackProvider
    {
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FlagsFallbackSnapshot?> LoadAsync(FlagsFallbackProviderContext context, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(context, cancellationToken);

        public async Task SaveAsync(FlagsFallbackProviderContext context, FlagsFallbackSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.SaveAsync(context, snapshot, cancellationToken);
                Saved.TrySetResult();
            }
            catch (Exception error)
            {
                Saved.TrySetException(error);
                throw;
            }
        }
    }
}
