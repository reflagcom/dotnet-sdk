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
        var body = JsonNode.Parse(Response((Flag("flag"), false)))!;
        var definition = body["features"]![0]!.AsObject();
        definition.Remove("secret");
        if (secret is not null) definition["secret"] = JsonNode.Parse(secret);
        var transport = new TestTransport();
        transport.EnqueueGetJson(body.ToJsonString());
        await using var client = new ReflagClient(Options(transport));
        await client.InitializeAsync();
        var bootstrap = client.BindClient(Context, Quiet).GetFlagsForBootstrap();
        Assert.Equal(expectedPublic, bootstrap.Flags.ContainsKey("flag"));
        Assert.Equal(1, bootstrap.FlagStateVersion);
        Assert.True(client.GetFlag("flag", Context, Quiet));
        Assert.Empty(transport.PostCalls);
    }

    [Fact]
    public async Task Overrides_cannot_publish_private_or_unknown_keys()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(Response((Flag("public"), false), (Flag("private"), true)));
        await using var client = new ReflagClient(Options(transport));
        await client.InitializeAsync();
        client.SetFlagOverrides(new Dictionary<string, bool> { ["public"] = false, ["private"] = false, ["override-only"] = true });
        var bootstrap = client.GetFlagsForBootstrap(Context, Quiet);
        var flag = Assert.Single(bootstrap.Flags).Value;
        Assert.Equal("public", flag.Key);
        Assert.False(flag.Value);
        Assert.Null(flag.TargetingVersion);
        Assert.False(client.GetFlag("private", Context, Quiet));
        Assert.True(client.GetFlag("override-only", Context, Quiet));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bootstrap_visibility_comes_from_the_evaluated_definitions(bool oldPublic)
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(Response((Flag("flag"), !oldPublic)));
        var newer = JsonNode.Parse(Response((Flag("flag"), oldPublic)))!;
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
            transport.EnqueueGetJson(Response((Flag("public"), false), (Flag("private"), true)));
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

            foreach (var definition in saved["flags"]!.AsArray()) definition!.AsObject().Remove("secret");
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

    private static FlagDefinition Flag(string key) => TestDefinitions.CreateFlag(key, 1, new FlagConstantFilterDefinition { Value = true });

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
        var body = JsonSerializer.SerializeToNode(new { success = true, flagStateVersion = 1, features = flags.Select(pair => pair.Flag).ToArray() }, Reflag.Internal.ReflagJson.Options)!;
        for (var index = 0; index < flags.Length; index++) body["features"]![index]!["secret"] = flags[index].Secret;
        return body.ToJsonString();
    }
}
