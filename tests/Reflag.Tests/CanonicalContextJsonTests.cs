using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class CanonicalContextJsonTests
{
    [Fact]
    public async Task Check_deduplication_preserves_context_types_and_ignores_object_key_order()
    {
        var transport = new TestTransport();
        transport.EnqueueGetJson("{\"success\":true,\"features\":[]}");
        await using var client = new ReflagClient(new ReflagClientOptions
        {
            SecretKey = "validSecretKeyWithMoreThan22Chars",
            HttpClient = transport.CreateHttpClient(),
            FlagsSyncMode = ReflagFlagsSyncMode.Polling,
        });
        await client.InitializeAsync();
        foreach (var value in new object?[] { 1, "1", null, "", Array.Empty<string>(), new[] { "a", "b" }, new[] { "b", "a" } })
        {
            client.GetFlag("flag", new ReflagContext { Other = new Dictionary<string, object?> { ["value"] = value } });
        }
        client.GetFlag("flag", ReflagContext.From(new { Other = new { b = 2, a = 1 } }));
        client.GetFlag("flag", ReflagContext.From(new { Other = new { a = 1, b = 2 } }));
        await client.FlushAsync();
        using var payload = JsonDocument.Parse(Assert.Single(transport.PostCalls).Body);
        Assert.Equal(8, payload.RootElement.GetArrayLength());
    }

    [Fact]
    public void Sorts_objects_recursively_including_inside_arrays_without_changing_types()
    {
        var context = new Dictionary<string, object?>
        {
            ["z"] = new object?[] { new { z = false, a = 1 }, null, "1" },
            ["a"] = new { z = "value", a = Array.Empty<string>() },
        };
        Assert.Equal("{\"a\":{\"a\":[],\"z\":\"value\"},\"z\":[{\"a\":1,\"z\":false},null,\"1\"]}", CanonicalContextJson.Serialize(context));
    }

    [Fact]
    public void Distinguishes_dotted_keys_from_nested_objects_and_array_indices()
    {
        var contexts = new[]
        {
            new Dictionary<string, object?> { ["a.b"] = "value" },
            new Dictionary<string, object?> { ["a"] = new { b = "value" } },
            new Dictionary<string, object?> { ["a"] = new[] { "value" } },
            new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?> { ["0"] = "value" } },
        };
        Assert.Equal(contexts.Length, contexts.Select(CanonicalContextJson.Serialize).Distinct().Count());
    }
}
