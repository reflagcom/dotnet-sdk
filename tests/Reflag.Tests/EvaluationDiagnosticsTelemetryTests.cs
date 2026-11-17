using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Reflag.Tests;

public sealed class EvaluationDiagnosticsTelemetryTests
{
    private static readonly JsonSerializerOptions BootstrapJsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("GT", "private-context", "private-targeting")]
    [InlineData("DATE_AFTER", "private-context", "private-targeting")]
    [InlineData("BEFORE", "2024-01-10", "private-targeting")]
    public async Task Scalar_diagnostics_reach_checks_bootstrap_and_rate_limited_warnings(
        string op, string contextValue, string targetingValue)
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(Definitions(op, targetingValue));
        var logger = new TestLogger();
        await using var client = new ReflagClient(Options(transport, logger));
        await client.InitializeAsync();
        var context = Context(contextValue);

        Assert.False(client.GetFlag("flag", context));
        Assert.False(client.GetFlag("flag", context));
        // Different values with the same diagnostic identities should not log another warning.
        Assert.False(client.GetFlag("flag", Context(contextValue == "private-context" ? "different-private-context" : contextValue)));
        Assert.True(client.GetFlag("healthy", new ReflagContext()));
        var bootstrap = client.GetFlagsForBootstrap(context);
        var errors = bootstrap.Flags["flag"].Errors!;
        Assert.Equal(new[] { "INVALID_COMPARISON" }, errors.Select(error => error.Code));
        Assert.Empty(bootstrap.Flags["healthy"].Errors);
        using var bootstrapJson = JsonDocument.Parse(JsonSerializer.Serialize(bootstrap, BootstrapJsonOptions));
        var bootstrapErrors = bootstrapJson.RootElement.GetProperty("flags").GetProperty("flag").GetProperty("errors");

        await client.FlushAsync();
        using var payload = JsonDocument.Parse(Assert.Single(transport.PostCalls).Body);
        var check = payload.RootElement.EnumerateArray().First(item => item.GetProperty("key").GetString() == "flag");
        Assert.Equal(bootstrapErrors.GetRawText(), check.GetProperty("evalErrors").GetRawText());
        Assert.False(check.TryGetProperty("evalMissingFields", out _));
        // The targeting rule failed; the catch-all default selected the "off" variant.
        Assert.Equal(new[] { false, true }, check.GetProperty("evalRuleResults").EnumerateArray().Select(item => item.GetBoolean()));
        Assert.Equal("off", check.GetProperty("evalResult").GetString());
        foreach (var error in check.GetProperty("evalErrors").EnumerateArray())
        {
            Assert.Equal("other.value", error.GetProperty("field").GetString());
            Assert.Equal(op, error.GetProperty("operator").GetString());
            Assert.DoesNotContain("private-context", error.GetProperty("message").GetString()!);
            Assert.DoesNotContain("private-targeting", error.GetProperty("message").GetString()!);
        }

        var healthy = payload.RootElement.EnumerateArray().Single(item => item.GetProperty("key").GetString() == "healthy");
        Assert.False(healthy.TryGetProperty("evalErrors", out _));
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("flag targeting rules"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Checks_before_initialization_include_a_diagnostic_even_for_overrides(bool? overrideValue)
    {
        var transport = new TestTransport();
        var logger = new TestLogger();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
            Logger = logger,
            FlagOverrides = overrideValue.HasValue ? new Dictionary<string, bool> { ["flag"] = overrideValue.Value } : null,
        });

        Assert.Equal(overrideValue ?? false, client.BindClient(new ReflagContext()).GetFlag("flag"));
        await client.FlushAsync();
        using var payload = JsonDocument.Parse(Assert.Single(transport.PostCalls).Body);
        var check = Assert.Single(payload.RootElement.EnumerateArray());
        var error = Assert.Single(check.GetProperty("evalErrors").EnumerateArray());
        Assert.Equal("CLIENT_NOT_INITIALIZED", error.GetProperty("code").GetString());
        Assert.Equal(string.Empty, error.GetProperty("field").GetString());
        Assert.Equal("ReflagClient was not initialized before this flag was evaluated. Call InitializeAsync() before evaluating flags.", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("operator", out _));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("not initialized"));
    }

    [Fact]
    public async Task Initialization_diagnostic_is_appended_to_check_errors_but_not_bootstrap_errors()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(Definitions("GT", "private-targeting"));
        var logger = new TestLogger();
        await using var client = new ReflagClient(Options(transport, logger));
        // Definitions may already be cached even though InitializeAsync has not finished.
        await client.RefreshFlagsAsync();
        var context = Context("private-context");
        Assert.False(client.GetFlag("flag", context));
        Assert.True(client.GetFlag("healthy", context));
        var bootstrap = client.GetFlagsForBootstrap(context);
        Assert.Equal(new[] { "INVALID_COMPARISON" }, bootstrap.Flags["flag"].Errors.Select(error => error.Code));
        Assert.Empty(bootstrap.Flags["healthy"].Errors);

        await client.FlushAsync();
        using var payload = JsonDocument.Parse(Assert.Single(transport.PostCalls).Body);
        var check = payload.RootElement.EnumerateArray().Single(item => item.GetProperty("key").GetString() == "flag");
        Assert.Equal(new[] { "INVALID_COMPARISON", "CLIENT_NOT_INITIALIZED" },
            check.GetProperty("evalErrors").EnumerateArray().Select(error => error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Unknown_operators_reject_the_collection()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson(Definitions("FUTURE_OPERATOR", "private-targeting"));
        var logger = new TestLogger();
        await using var client = new ReflagClient(Options(transport, logger));
        await client.InitializeAsync();

        Assert.Empty(client.GetFlagDefinitions().Flags);
        Assert.False(client.GetFlag("healthy", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));
        Assert.Single(transport.GetCalls);
    }

    [Fact]
    public async Task Disabled_telemetry_does_not_send_pre_initialization_checks()
    {
        var transport = new TestTransport();
        await using var client = new ReflagClient(Options(transport, new TestLogger()));
        Assert.False(client.GetFlag("flag", new ReflagContext(), new ReflagTelemetryOptions { EnableTelemetry = false }));
        await client.FlushAsync();
        Assert.Empty(transport.PostCalls);
    }

    [Fact]
    public async Task Offline_access_before_initialization_does_not_log_initialization_errors_or_send_checks()
    {
        var transport = new TestTransport();
        var logger = new TestLogger();
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            Offline = true,
            HttpClient = transport.CreateHttpClient(),
            Logger = logger,
            FlagOverrides = new Dictionary<string, bool> { ["flag"] = true },
        });

        Assert.True(client.GetFlag("flag", new ReflagContext()));
        Assert.Empty(client.GetFlagsForBootstrap(new ReflagContext()).Flags["flag"].Errors);
        await client.FlushAsync();
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Empty(transport.GetCalls);
        Assert.Empty(transport.PostCalls);
    }

    private static ReflagClientOptions Options(TestTransport transport, TestLogger logger) => new()
    {
        SecretKey = "validSecretKeyWithMoreThan22Chars",
        HttpClient = transport.CreateHttpClient(),
        FlagsSyncMode = ReflagFlagsSyncMode.Polling,
        FlagsFetchRetries = 0,
        Logger = logger,
    };

    private static ReflagContext Context(string value) => new()
    {
        Other = new Dictionary<string, object?> { ["value"] = value },
    };

    private static string Definitions(string op, string value) => JsonSerializer.Serialize(new
    {
        success = true,
        protocolVersion = 2,
        flagStateVersion = 1,
        flags = new object[]
        {
            BooleanFlag("flag", new { type = "context", field = "other.value", @operator = op, values = new[] { value } }),
            BooleanFlag("healthy", new { type = "constant", value = true }),
        },
        segments = Array.Empty<object>(),
    });

    private static object BooleanFlag(string key, object filter) => new
    {
        key,
        sourceVersionId = $"{key}:v1",
        variants = new { on = true, off = false },
        rules = new object[]
        {
            new { id = "rule-0", filter, result = new { type = "variant", variantKey = "on" } },
            new { id = "default", filter = new { type = "constant", value = true }, result = new { type = "variant", variantKey = "off" } },
        },
    };
}
