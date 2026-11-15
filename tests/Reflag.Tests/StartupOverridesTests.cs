using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Reflag.Tests;

// Environment overrides are process-global; do not run these tests alongside other client constructors.
[CollectionDefinition("Startup overrides", DisableParallelization = true)]
public sealed class StartupOverridesTestGroup;

[Collection("Startup overrides")]
public sealed class StartupOverridesTests
{
    private sealed record ModelConfig(string Name, int MaxTokens);

    [Fact]
    public async Task Json_values_are_captured_at_construction_and_remain_unresolved()
    {
        ReflagClient client;
        var node = JsonNode.Parse("{\"name\":\"node\"}")!;
        var tags = new[] { "one", "two" };
        var values = new Dictionary<string, object?>
        {
            ["model"] = new ModelConfig("large", 8000),
            ["enabled"] = true,
            ["disabled"] = false,
            ["limit"] = 0,
            ["label"] = "",
            ["none"] = null,
            ["node"] = node,
            ["tags"] = tags,
        };
        using (var document = JsonDocument.Parse("{\"name\":\"element\"}"))
        {
            values["element"] = document.RootElement;
            client = new ReflagClient(new ReflagClientOptions { Offline = true, JsonFlagOverrides = values });
        }

        await using (client)
        {
            values.Clear();
            node["name"] = "mutated";
            tags[0] = "mutated";
            await client.InitializeAsync();

            Assert.Equal(new ModelConfig("large", 8000), client.GetFlag("model", new ReflagContext(), new ModelConfig("fallback", 0)));
            Assert.True(client.GetFlag("enabled", new ReflagContext()));
            Assert.False(client.GetFlag("disabled", new ReflagContext(), true));
            Assert.Equal(0, client.GetFlag("limit", new ReflagContext(), 10));
            Assert.Equal("", client.GetFlag("label", new ReflagContext(), "fallback"));
            Assert.Equal(JsonValueKind.Null, client.GetFlag<JsonElement>("none", new ReflagContext(), default).ValueKind);
            Assert.Equal("node", client.GetFlag<JsonElement>("node", new ReflagContext(), default).GetProperty("name").GetString());
            Assert.Equal("element", client.GetFlag<JsonElement>("element", new ReflagContext(), default).GetProperty("name").GetString());
            Assert.Equal(new[] { "one", "two" }, client.GetFlag("tags", new ReflagContext(), Array.Empty<string>()));
            Assert.All(client.GetFlagsForBootstrap(new ReflagContext()).Flags.Values, flag =>
            {
                Assert.False(flag.Resolved);
                Assert.Null(flag.VariantKey);
                Assert.Null(flag.SourceVersionId);
                Assert.Empty(flag.RuleResults);
                Assert.Empty(flag.Errors);
            });
        }
    }

    [Fact]
    public async Task Json_factory_is_lazy_and_uses_each_bound_context()
    {
        var seenContexts = new List<ReflagContext>();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            JsonFlagOverridesFactory = context =>
            {
                seenContexts.Add(context);
                return new Dictionary<string, object?>
                {
                    ["model"] = new ModelConfig(context.Company?.Id ?? "anonymous", 1000),
                    ["enabled"] = context.User?.Id == "user-1",
                };
            },
        });
        await client.InitializeAsync();
        Assert.Empty(seenContexts);

        var input = ReflagContext.From(new { User = new { Id = "user-1" }, Company = new { Id = "company-1" } });
        var bound = client.BindClient(input);
        Assert.Equal(new ModelConfig("company-1", 1000), bound.GetFlag("model", new ModelConfig("fallback", 0)));
        Assert.NotSame(input, seenContexts[0]);
        Assert.Equal("company-1", seenContexts[0].Company?.Id);
        Assert.True(bound.GetFlag("enabled"));
        Assert.Equal("anonymous", client.GetFlag("model", new ReflagContext(), new ModelConfig("fallback", 0)).Name);
        Assert.False(client.GetFlag("enabled", new ReflagContext()));
        Assert.Equal("company-1", bound.GetFlag("model", new ModelConfig("fallback", 0)).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Boolean_constructor_sources_still_work(bool factory)
    {
        var values = new Dictionary<string, bool> { ["enabled"] = true, ["disabled"] = false };
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            FlagOverrides = factory ? null : values,
            FlagOverridesFactory = factory ? _ => values : null,
        });

        Assert.True(client.GetFlag("enabled", new ReflagContext()));
        Assert.False(client.GetFlag("disabled", new ReflagContext(), true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Json_constructor_sources_override_environment_and_support_runtime_layers(bool factory)
    {
        var originalEnabled = Environment.GetEnvironmentVariable("REFLAG_FLAGS_ENABLED");
        var originalDisabled = Environment.GetEnvironmentVariable("REFLAG_FLAGS_DISABLED");
        try
        {
            Environment.SetEnvironmentVariable("REFLAG_FLAGS_ENABLED", "startup-env-only,startup-shared");
            Environment.SetEnvironmentVariable("REFLAG_FLAGS_DISABLED", "startup-env-disabled");
            var values = new Dictionary<string, object?> { ["startup-shared"] = false, ["startup-env-disabled"] = "json" };
            await using var client = new ReflagClient(new ReflagClientOptions
            {
                Offline = true,
                JsonFlagOverrides = factory ? null : values,
                JsonFlagOverridesFactory = factory ? _ => values : null,
            });
            await client.InitializeAsync();

            Assert.True(client.GetFlag("startup-env-only", new ReflagContext()));
            Assert.False(client.GetFlag("startup-shared", new ReflagContext()));
            Assert.Equal("json", client.GetFlag("startup-env-disabled", new ReflagContext(), "fallback"));
            using (client.PushFlagOverrides(new Dictionary<string, bool> { ["startup-shared"] = true }))
            {
                Assert.True(client.GetFlag("startup-shared", new ReflagContext()));
            }

            Assert.False(client.GetFlag("startup-shared", new ReflagContext()));
            client.SetFlagOverrides(new Dictionary<string, object?> { ["startup-shared"] = "runtime" });
            Assert.Equal("runtime", client.GetFlag("startup-shared", new ReflagContext(), "fallback"));
            Assert.False(client.GetFlag("startup-env-only", new ReflagContext()));
            client.ClearFlagOverrides();
            Assert.Empty(client.GetFlags(new ReflagContext()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("REFLAG_FLAGS_ENABLED", originalEnabled);
            Environment.SetEnvironmentVariable("REFLAG_FLAGS_DISABLED", originalDisabled);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_factory_results_preserve_environment_overrides(bool json)
    {
        var original = Environment.GetEnvironmentVariable("REFLAG_FLAGS_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("REFLAG_FLAGS_ENABLED", "startup-env-only");
            await using var client = new ReflagClient(new ReflagClientOptions
            {
                Offline = true,
                FlagOverridesFactory = json ? null : _ => null!,
                JsonFlagOverridesFactory = json ? _ => null! : null,
            });
            Assert.True(client.GetFlag("startup-env-only", new ReflagContext()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("REFLAG_FLAGS_ENABLED", original);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Null_runtime_factories_are_normalized_for_base_and_temporary_overrides(bool json)
    {
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            JsonFlagOverrides = new Dictionary<string, object?> { ["model"] = "base" },
        });
        Func<ReflagContext, IReadOnlyDictionary<string, bool>> booleanFactory = _ => null!;
        Func<ReflagContext, IReadOnlyDictionary<string, object?>> jsonFactory = _ => null!;
        using (json ? client.PushFlagOverrides(jsonFactory) : client.PushFlagOverrides(booleanFactory))
        {
            Assert.Equal("base", client.GetFlag("model", new ReflagContext(), "fallback"));
        }

        if (json)
        {
            client.SetFlagOverrides(jsonFactory);
        }
        else
        {
            client.SetFlagOverrides(booleanFactory);
        }

        Assert.Empty(client.GetFlags(new ReflagContext()));
    }

    [Fact]
    public void Undefined_json_elements_fail_during_construction()
    {
        Assert.Throws<InvalidOperationException>(() => new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            JsonFlagOverrides = new Dictionary<string, object?> { ["invalid"] = default(JsonElement) },
        }));
    }

    [Fact]
    public async Task Undefined_json_elements_do_not_replace_valid_overrides()
    {
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            JsonFlagOverrides = new Dictionary<string, object?> { ["model"] = "base" },
        });
        var invalid = new Dictionary<string, object?> { ["model"] = "replacement", ["invalid"] = default(JsonElement) };

        Assert.Throws<InvalidOperationException>(() => client.SetFlagOverrides(invalid));
        Assert.Throws<InvalidOperationException>(() => client.PushFlagOverrides(invalid));
        Assert.Throws<InvalidOperationException>(() => ReflagFallbackProviders.Static(invalid));
        Assert.Equal("base", client.GetFlag("model", new ReflagContext(), "fallback"));
        _ = JsonSerializer.Serialize(client.GetFlagsForBootstrap(new ReflagContext()));
    }

    [Fact]
    public void Non_serializable_startup_values_fail_during_construction()
    {
        Assert.Throws<NotSupportedException>(() => new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            JsonFlagOverrides = new Dictionary<string, object?> { ["invalid"] = new Action(() => { }) },
        }));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(15)]
    public void Constructor_rejects_conflicting_override_sources(int mask)
    {
        var options = new ReflagClientOptions
        {
            Offline = true,
            FlagOverrides = (mask & 1) != 0 ? new Dictionary<string, bool>() : null,
            FlagOverridesFactory = (mask & 2) != 0 ? _ => new Dictionary<string, bool>() : null,
            JsonFlagOverrides = (mask & 4) != 0 ? new Dictionary<string, object?>() : null,
            JsonFlagOverridesFactory = (mask & 8) != 0 ? _ => new Dictionary<string, object?>() : null,
        };

        var error = Assert.Throws<ArgumentException>(() => new ReflagClient(options));
        Assert.Equal("options", error.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dependency_injection_supports_json_startup_overrides(bool configure)
    {
        var options = new ReflagClientOptions
        {
            Offline = true,
            JsonFlagOverrides = new Dictionary<string, object?> { ["model"] = new ModelConfig("di", 1000) },
        };
        var services = new ServiceCollection();
        if (configure)
        {
            services.AddReflag(_ => options);
        }
        else
        {
            services.AddReflag(options);
        }

        await using var provider = services.BuildServiceProvider();
        await provider.GetServices<IHostedService>().Single().StartAsync(CancellationToken.None);
        var client = provider.GetRequiredService<ReflagClient>();
        Assert.Equal(new ModelConfig("di", 1000), client.BindClient(new ReflagContext()).GetFlag("model", new ModelConfig("fallback", 0)));
    }

    [Fact]
    public async Task Startup_json_overrides_do_not_report_an_authored_selection()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(TestDefinitions.FlagsResponse(5, TestDefinitions.CreateMultivariateFlag(
            "model", "quality", new Dictionary<string, object?> { ["quality"] = "live" })));
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            FlagsFetchRetries = 0,
            JsonFlagOverrides = new Dictionary<string, object?> { ["model"] = "startup" },
        });
        await client.InitializeAsync();

        Assert.Equal("startup", client.GetFlag("model", new ReflagContext(), "fallback"));
        await client.FlushAsync();

        using var payload = JsonDocument.Parse(Assert.Single(transport.PostCalls).Body);
        var check = Assert.Single(payload.RootElement.EnumerateArray());
        Assert.False(check.TryGetProperty("evalResult", out _));
        Assert.False(check.TryGetProperty("sourceVersionId", out _));
        Assert.False(check.TryGetProperty("matchedRuleId", out _));
        Assert.Equal(5, check.GetProperty("flagStateVersion").GetInt32());
        Assert.False(client.GetFlagsForBootstrap(new ReflagContext()).Flags["model"].Resolved);
    }
}
