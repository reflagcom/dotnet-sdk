using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class FlagEvaluationContextTests
{
    [Fact]
    public void FlattenJson_returns_empty_dictionary_for_empty_object()
    {
        var flattened = FlagEvaluation.FlattenJson(new Dictionary<string, object?>());

        Assert.Empty(flattened);
    }

    [Fact]
    public void FlattenJson_flattens_nested_objects_arrays_and_nulls()
    {
        var flattened = FlagEvaluation.FlattenJson(new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?>
            {
                ["id"] = "u1",
                ["tags"] = new object?[] { "a", null, "c" },
            },
            ["company"] = new Dictionary<string, object?>(),
            ["other"] = null,
        });

        Assert.Equivalent(
            new Dictionary<string, string>
            {
                ["user.id"] = "u1",
                ["user.tags.0"] = "a",
                ["user.tags.1"] = string.Empty,
                ["user.tags.2"] = "c",
                ["company"] = string.Empty,
                ["other"] = string.Empty,
            },
            flattened);
    }

    [Fact]
    public void FlattenJson_preserves_special_character_keys_and_scalar_formatting()
    {
        var flattened = FlagEvaluation.FlattenJson(new Dictionary<string, object?>
        {
            ["key.with.dots"] = "value1",
            ["key-with-dashes"] = "value2",
            ["key with spaces"] = "value3",
            ["numbers"] = new Dictionary<string, object?>
            {
                ["zero"] = 0,
                ["float"] = 3.14,
                ["infinity"] = double.PositiveInfinity,
                ["nan"] = double.NaN,
            },
            ["flags"] = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["disabled"] = false,
            },
        });

        Assert.Equivalent(
            new Dictionary<string, string>
            {
                ["key.with.dots"] = "value1",
                ["key-with-dashes"] = "value2",
                ["key with spaces"] = "value3",
                ["numbers.zero"] = "0",
                ["numbers.float"] = "3.14",
                ["numbers.infinity"] = "Infinity",
                ["numbers.nan"] = "NaN",
                ["flags.enabled"] = "true",
                ["flags.disabled"] = "false",
            },
            flattened);
    }

    [Fact]
    public void FlattenJson_handles_empty_arrays_empty_nested_objects_and_skips_null_only_by_converting_to_empty_string()
    {
        var flattened = FlagEvaluation.FlattenJson(new Dictionary<string, object?>
        {
            ["array"] = Array.Empty<object?>(),
            ["nested"] = new Dictionary<string, object?>
            {
                ["empty"] = new Dictionary<string, object?>(),
                ["present"] = "value",
            },
            ["nullable"] = null,
        });

        Assert.Equivalent(
            new Dictionary<string, string>
            {
                ["array"] = string.Empty,
                ["nested.empty"] = string.Empty,
                ["nested.present"] = "value",
                ["nullable"] = string.Empty,
            },
            flattened);
    }

    [Fact]
    public void UnflattenJson_reconstructs_nested_objects()
    {
        var unflattened = FlagEvaluation.UnflattenJson(new Dictionary<string, object?>
        {
            ["a.b.c"] = "value",
            ["x.y"] = "anotherValue",
        });

        Assert.Equivalent(
            new Dictionary<string, object?>
            {
                ["a"] = new Dictionary<string, object?>
                {
                    ["b"] = new Dictionary<string, object?>
                    {
                        ["c"] = "value",
                    },
                },
                ["x"] = new Dictionary<string, object?>
                {
                    ["y"] = "anotherValue",
                },
            },
            unflattened,
            strict: true);
    }

    [Fact]
    public void UnflattenJson_keeps_array_like_keys_as_object_properties()
    {
        var unflattened = FlagEvaluation.UnflattenJson(new Dictionary<string, object?>
        {
            ["arr.0"] = "first",
            ["arr.1"] = "second",
        });

        Assert.Equivalent(
            new Dictionary<string, object?>
            {
                ["arr"] = new Dictionary<string, object?>
                {
                    ["0"] = "first",
                    ["1"] = "second",
                },
            },
            unflattened,
            strict: true);
    }

    [Fact]
    public void UnflattenJson_ignores_overlapping_nested_keys_below_primitive_nodes()
    {
        var unflattened = FlagEvaluation.UnflattenJson(new Dictionary<string, object?>
        {
            ["a.b"] = "value1",
            ["a.b.c"] = "value2",
        });

        Assert.Equivalent(
            new Dictionary<string, object?>
            {
                ["a"] = new Dictionary<string, object?>
                {
                    ["b"] = "value1",
                },
            },
            unflattened,
            strict: true);
    }

    [Fact]
    public void UnflattenJson_keeps_empty_root_key()
    {
        var unflattened = FlagEvaluation.UnflattenJson(new Dictionary<string, object?>
        {
            [""] = "rootValue",
        });

        Assert.Equivalent(
            new Dictionary<string, object?>
            {
                [""] = "rootValue",
            },
            unflattened,
            strict: true);
    }
}
