using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Reflag.Tests;

public sealed class BootstrapPrivacyTests
{
    private static readonly ReflagContext Context = new() { User = new ReflagUserContext { Id = "synthetic-user" } };
    private static readonly ReflagTelemetryOptions Quiet = new() { EnableTelemetry = false };

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", true)]
    [InlineData("true", false)]
    [InlineData("null", false)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("\"false\"", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    public async Task Bootstrap_requires_explicit_public_visibility(string? secret, bool expectedPublic)
    {
        var flag = TestDefinitions.CreateMultivariateFlag("flag", "on", new Dictionary<string, object?>
        {
            ["on"] = new { marker = "synthetic-server-only" },
        });
        var body = JsonNode.Parse(TestDefinitions.FlagsResponse(1, flag))!;
        var definition = body["flags"]![0]!.AsObject();
        definition.Remove("secret");
        if (secret is not null)
        {
            definition["secret"] = JsonNode.Parse(secret);
        }

        var transport = new TestTransport();
        transport.EnqueueGetJson(body.ToJsonString());
        await using var client = new ReflagClient(Options(transport));
        await client.InitializeAsync();
        var bootstrap = client.BindClient(Context, Quiet).GetFlagsForBootstrap();
        Assert.Equal(expectedPublic, bootstrap.Flags.ContainsKey("flag"));
        Assert.Equal(1, bootstrap.FlagStateVersion);
        Assert.Equal("synthetic-server-only", client.GetFlag<JsonElement>("flag", Context, default, Quiet).GetProperty("marker").GetString());
        Assert.Empty(transport.PostCalls);
    }

    [Fact]
    public async Task Overrides_do_not_publish_private_or_unknown_keys_and_private_dependencies_still_work()
    {
        var privateFlag = TestDefinitions.CreateMultivariateFlag("private", "on", new Dictionary<string, object?> { ["on"] = new { marker = "private" } });
        var publicFlag = TestDefinitions.CreateFlag("public", 1, new FlagReferenceFilterDefinition { Key = "private", VariantKey = "on" });
        var transport = new TestTransport();
        transport.EnqueueGetJson(Response((publicFlag, false), (privateFlag, true)));
        await using var client = new ReflagClient(Options(transport));
        await client.InitializeAsync();
        Assert.True(client.GetFlagsForBootstrap(Context, Quiet).Flags["public"].Value!.Value.GetBoolean());
        client.SetFlagOverrides(new Dictionary<string, object?>
        {
            ["public"] = new { visible = true },
            ["private"] = "still-private",
            ["override-only"] = true,
        });
        var bootstrap = client.GetFlagsForBootstrap(Context, Quiet);
        var flag = Assert.Single(bootstrap.Flags).Value;
        Assert.Equal("public", flag.FlagKey);
        Assert.True(flag.Value!.Value.GetProperty("visible").GetBoolean());
        Assert.False(flag.Resolved);
        Assert.Null(flag.SourceVersionId);
        Assert.Null(flag.VariantKey);
        Assert.Equal("still-private", client.GetFlag("private", Context, "", Quiet));
        Assert.True(client.GetFlag("override-only", Context, Quiet));
    }

    [Theory]
    [InlineData("flag")]
    [InlineData("segment")]
    public async Task Bootstrap_redacts_dependency_sources_without_changing_server_checks(string kind)
    {
        var condition = new FlagContextFilterDefinition { Field = "company.plan", Operator = FlagContextFilterOperator.Is, Values = ["pro"] };
        var privateFlag = TestDefinitions.CreateFlag("private-dependency", 1, condition);
        FlagFilterDefinition reference = kind == "flag"
            ? new FlagReferenceFilterDefinition { Key = "private-dependency", VariantKey = "on" }
            : new SegmentReferenceFilterDefinition { Key = "private-dependency" };
        var publicFlag = TestDefinitions.CreateFlag("public", 1, reference);
        var body = JsonNode.Parse(Response((publicFlag, false), (privateFlag, true)))!;
        if (kind == "segment")
        {
            body["segments"] = JsonSerializer.SerializeToNode(new[] { new SegmentDefinition { Key = "private-dependency", Filter = condition } }, Reflag.Internal.ReflagJson.Options);
        }

        var transport = new TestTransport();
        transport.EnqueueGetJson(body.ToJsonString());
        await using var client = new ReflagClient(Options(transport));
        await client.InitializeAsync();
        var bootstrap = client.GetFlagsForBootstrap(new ReflagContext(), Quiet);
        var error = Assert.Single(Assert.Single(bootstrap.Flags).Value.Errors);
        Assert.Equal("MISSING_CONTEXT_FIELD", error.Code);
        Assert.Null(error.Source);
        Assert.DoesNotContain("private-dependency", JsonSerializer.Serialize(bootstrap));
        client.GetFlag("public", new ReflagContext());
        await client.FlushAsync();
        using var bulk = JsonDocument.Parse(Assert.Single(transport.PostCalls).Body);
        var source = Assert.Single(bulk.RootElement.EnumerateArray()).GetProperty("evalErrors")[0].GetProperty("source");
        Assert.Equal(kind, source.GetProperty("type").GetString());
        Assert.Equal("private-dependency", source.GetProperty("key").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bootstrap_visibility_comes_from_the_evaluated_snapshot(bool oldPublic)
    {
        var flag = TestDefinitions.CreateFlag("flag", 1, new FlagConstantFilterDefinition { Value = true });
        var transport = new TestTransport();
        transport.EnqueueGetJson(Response((flag, !oldPublic)));
        var newer = JsonNode.Parse(Response((flag, oldPublic)))!;
        newer["flagStateVersion"] = 2;
        transport.EnqueueGetJson(newer.ToJsonString());
        await using var client = new ReflagClient(Options(transport));
        await client.InitializeAsync();
        var changed = false;
        client.SetFlagOverrides(_ =>
        {
            if (!changed)
            {
                changed = true;
                client.RefreshFlagsAsync(2).GetAwaiter().GetResult();
            }

            return new Dictionary<string, bool>();
        });
        var bootstrap = client.GetFlagsForBootstrap(Context, Quiet);
        Assert.Equal(1, bootstrap.FlagStateVersion);
        Assert.Equal(oldPublic, bootstrap.Flags.ContainsKey("flag"));
        bootstrap = client.GetFlagsForBootstrap(Context, Quiet);
        Assert.Equal(2, bootstrap.FlagStateVersion);
        Assert.Equal(!oldPublic, bootstrap.Flags.ContainsKey("flag"));
    }

    [Fact]
    public async Task File_fallback_preserves_visibility_and_old_snapshots_fail_closed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"reflag-privacy-{Guid.NewGuid():N}");
        try
        {
            var provider = ReflagFallbackProviders.File(new FileFallbackProviderOptions { Directory = directory });
            var transport = new TestTransport();
            var publicFlag = TestDefinitions.CreateFlag("public", 1, new FlagConstantFilterDefinition { Value = true });
            var privateFlag = TestDefinitions.CreateFlag("private", 1, new FlagConstantFilterDefinition { Value = true });
            transport.EnqueueGetJson(Response((publicFlag, false), (privateFlag, true)));
            await using (var client = new ReflagClient(Options(transport, provider)))
            {
                await client.InitializeAsync();
                await client.FlushAsync();
            }

            var file = Assert.Single(Directory.GetFiles(directory));
            var saved = JsonNode.Parse(File.ReadAllText(file))!;
            Assert.False(saved["flags"]![0]!["secret"]!.GetValue<bool>());
            Assert.True(saved["flags"]![1]!["secret"]!.GetValue<bool>());
            var failing = new TestTransport();
            failing.EnqueueGetFailure(new HttpRequestException("offline"));
            await using (var restored = new ReflagClient(Options(failing, provider)))
            {
                await restored.InitializeAsync();
                Assert.Equal("public", Assert.Single(restored.GetFlagsForBootstrap(Context, Quiet).Flags).Key);
                Assert.True(restored.GetFlag("private", Context, Quiet));
            }

            foreach (var definition in saved["flags"]!.AsArray())
            {
                definition!.AsObject().Remove("secret");
            }

            File.WriteAllText(file, saved.ToJsonString());
            var oldTransport = new TestTransport();
            oldTransport.EnqueueGetFailure(new HttpRequestException("offline"));
            await using var old = new ReflagClient(Options(oldTransport, provider));
            await old.InitializeAsync();
            Assert.Empty(old.GetFlagsForBootstrap(Context, Quiet).Flags);
            Assert.True(old.GetFlag("private", Context, Quiet));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unclassified_static_values_and_offline_overrides_remain_server_only(bool offline)
    {
        var values = new Dictionary<string, bool> { ["local"] = true };
        var transport = new TestTransport();
        transport.EnqueueGetFailure(new HttpRequestException("offline"));
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "synthetic-key-with-more-than-22-characters",
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            FlagsFetchRetries = 0,
            Offline = offline,
            FlagOverrides = offline ? values : null,
            FlagsFallbackProvider = offline ? null : ReflagFallbackProviders.Static(values),
        });
        await client.InitializeAsync();
        Assert.Empty(client.GetFlagsForBootstrap(Context, Quiet).Flags);
        Assert.True(client.GetFlag("local", Context, Quiet));
    }

    private static ReflagClientOptions Options(TestTransport transport, IFlagsFallbackProvider? provider = null) => new()
    {
        SecretKey = "synthetic-key-with-more-than-22-characters",
        HttpClient = transport.CreateHttpClient(),
        FlagsSyncMode = ReflagFlagsSyncMode.Polling,
        FlagsFetchRetries = 0,
        FlagsFallbackProvider = provider,
    };

    private static string Response(params (FlagDefinition Flag, bool Secret)[] flags)
    {
        var body = JsonNode.Parse(TestDefinitions.FlagsResponse(1, flags.Select(pair => pair.Flag).ToArray()))!;
        for (var index = 0; index < flags.Length; index++) body["flags"]![index]!["secret"] = flags[index].Secret;
        return body.ToJsonString();
    }
}
