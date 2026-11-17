using System.Text.Json;
using System.Text.Json.Nodes;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class FlagCollectionEvaluatorTests
{
    private static readonly Lazy<JsonDocument> Parity = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "evaluator-v2-parity.json"))));

    public static TheoryData<string> ParitySuites()
    {
        var data = new TheoryData<string>();
        foreach (var suite in Parity.Value.RootElement.GetProperty("suites").EnumerateArray())
        {
            data.Add(suite.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ParitySuites))]
    public void MatchesReferenceEvaluatorResults(string suiteName)
    {
        var suite = Parity.Value.RootElement.GetProperty("suites").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == suiteName);
        var collection = suite.GetProperty("collection");
        // The reference evaluator is exercised directly, including definitions the SDK would reject.
        var evaluator = new FlagCollectionEvaluator(
            JsonSerializer.Deserialize<FlagDefinition[]>(collection.GetProperty("flags").GetRawText(), ReflagJson.Options)!,
            JsonSerializer.Deserialize<SegmentDefinition[]>(collection.GetProperty("segments").GetRawText(), ReflagJson.Options)!);

        var caseIndex = 0;
        var mismatches = new List<string>();
        var divergences = 0;
        foreach (var testCase in suite.GetProperty("cases").EnumerateArray())
        {
            var context = (IReadOnlyDictionary<string, object?>)ToClr(testCase.GetProperty("context"))!;
            var actual = evaluator.EvaluateAll(context);
            var expected = testCase.GetProperty("flags");

            Assert.Equal(
                expected.EnumerateObject().Select(property => property.Name),
                actual.Keys);
            foreach (var property in expected.EnumerateObject())
            {
                var expectedJson = JsonNode.Parse(property.Value.GetRawText());
                var actualJson = JsonSerializer.SerializeToNode(actual[property.Name], ReflagJson.Options);
                var singleJson = JsonSerializer.SerializeToNode(evaluator.EvaluateFlag(context, property.Name), ReflagJson.Options);
                if (!JsonNode.DeepEquals(expectedJson, actualJson) &&
                    IsKnownDateParsingDivergence(property.Name, ContextFlattening.FlattenContext(context)))
                {
                    // Non-ISO date strings are implementation-defined: V8 reads "42" as 2042 and "7.5" as
                    // 2001-07-05, while .NET rejects the former and reads the latter in the current year.
                    divergences++;
                    continue;
                }

                if (!JsonNode.DeepEquals(expectedJson, actualJson) || !JsonNode.DeepEquals(expectedJson, singleJson))
                {
                    mismatches.Add(
                        $"case {caseIndex} flag {property.Name} context {testCase.GetProperty("context").GetRawText()}\n" +
                        $"  expected: {expectedJson?.ToJsonString()}\n  actual:   {actualJson?.ToJsonString()}");
                }
            }

            caseIndex++;
        }

        Assert.True(mismatches.Count == 0, $"{suiteName}: {mismatches.Count} mismatches\n{string.Join("\n", mismatches)}");
        // Guard against the allowance silently growing.
        Assert.True(divergences <= 14, $"{suiteName}: {divergences} date-parsing divergences");
    }

    [Fact]
    public void HashMatchesReferenceCohorts()
    {
        foreach (var item in Parity.Value.RootElement.GetProperty("hashes").EnumerateArray())
        {
            Assert.Equal(item.GetProperty("value").GetInt32(), FlagCollectionEvaluator.HashInt(item.GetProperty("input").GetString()!));
        }
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        Assert.Contains("Duplicate flag key", Assert.Throws<ArgumentException>(() => Evaluator([Flag("a"), Flag("a")])).Message);
        Assert.Contains(
            "Duplicate segment key",
            Assert.Throws<ArgumentException>(() => Evaluator([Flag("a")], [Segment("s", Yes), Segment("s", Yes)])).Message);
    }

    [Fact]
    public void RejectsMissingReferencesAndUnknownVariantsEvenInUnreachableBranches()
    {
        var unreachableFlagReference = new FlagFilterGroupDefinition
        {
            Operator = "and",
            Filters = [new FlagConstantFilterDefinition { Value = false }, FlagReference("ghost")],
        };

        Assert.Contains("Unknown flag reference \"ghost\"", Assert.Throws<ArgumentException>(() => Evaluator([Flag("a", unreachableFlagReference)])).Message);
        Assert.Contains("Unknown segment reference \"ghost\"", Assert.Throws<ArgumentException>(() => Evaluator([Flag("a", SegmentReference("ghost"))])).Message);
        Assert.Contains(
            "Unknown variant \"missing\" in flag reference \"b\"",
            Assert.Throws<ArgumentException>(() => Evaluator([Flag("a", FlagReference("b", "missing")), Flag("b")])).Message);
    }

    [Fact]
    public void RejectsMixedFlagAndSegmentCycles()
    {
        var error = Assert.Throws<ArgumentException>(() => Evaluator(
            [Flag("a", SegmentReference("s")), Flag("b", FlagReference("a"))],
            [Segment("s", FlagReference("b"))]));
        Assert.Contains("Circular reference", error.Message);
    }

    [Fact]
    public void EvaluatingAnUnknownFlagThrows()
    {
        Assert.Throws<KeyNotFoundException>(() => Evaluator([Flag("a")]).EvaluateFlag(new Dictionary<string, object?>(), "missing"));
    }

    [Fact]
    public void ReturnsUndetachedValuesFromCallerOwnedDocuments()
    {
        FlagCollectionEvaluator evaluator;
        using (var document = JsonDocument.Parse("{\"model\":\"large\"}"))
        {
            evaluator = PreparedFlagCollection.Prepare(
                [new FlagDefinition
                {
                    Key = "model",
                    SourceVersionId = "v1",
                    Variants = new Dictionary<string, JsonElement> { ["large"] = document.RootElement },
                    Rules = [Rule("default", Yes, "large")],
                }],
                []).Evaluator;
        }

        var result = evaluator.EvaluateFlag(new Dictionary<string, object?>(), "model");
        Assert.Equal("large", result.Value!.Value.GetProperty("model").GetString());
    }

    internal static readonly FlagFilterDefinition Yes = new FlagConstantFilterDefinition { Value = true };

    internal static FlagFilterDefinition FlagReference(string key, string variantKey = "on") =>
        new FlagReferenceFilterDefinition { Key = key, VariantKey = variantKey };

    internal static FlagFilterDefinition SegmentReference(string key) => new SegmentReferenceFilterDefinition { Key = key };

    internal static SegmentDefinition Segment(string key, FlagFilterDefinition filter) => new() { Key = key, Filter = filter };

    internal static FlagRuleDefinition Rule(string id, FlagFilterDefinition filter, string variantKey) => new()
    {
        Id = id,
        Filter = filter,
        Result = new FlagVariantRuleResultDefinition { VariantKey = variantKey },
    };

    internal static FlagDefinition Flag(string key, params FlagFilterDefinition[] filters) => new()
    {
        Key = key,
        SourceVersionId = $"version-{key}",
        Variants = new Dictionary<string, JsonElement>
        {
            ["on"] = JsonSerializer.SerializeToElement(true),
            ["off"] = JsonSerializer.SerializeToElement(false),
        },
        Rules = filters.Select((filter, index) => Rule($"rule-{index}", filter, "on"))
            .Append(Rule("default", Yes, "off"))
            .ToArray(),
    };

    /// <summary>
    /// Date operators applied to bare numeric strings, whose parsing as dates is implementation-defined in both
    /// runtimes. All other parity vectors must match exactly.
    /// </summary>
    private static bool IsKnownDateParsingDivergence(string flagKey, IReadOnlyDictionary<string, object?> flattenedContext)
    {
        var separator = flagKey.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        var @operator = flagKey.Substring(0, separator);
        if (@operator is not ("AFTER" or "BEFORE" or "DATE_AFTER" or "DATE_BEFORE"))
        {
            return false;
        }

        return flattenedContext.TryGetValue($"company.{flagKey.Substring(separator + 1)}", out var value) &&
               value is string text &&
               text.Trim().Length > 0 &&
               double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static FlagCollectionEvaluator Evaluator(FlagDefinition[] flags, SegmentDefinition[]? segments = null) =>
        new(flags, segments ?? []);

    private static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => ToClr(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToClr).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
