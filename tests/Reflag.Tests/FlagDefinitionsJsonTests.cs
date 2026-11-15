using System.Text.Json;
using System.Text.Json.Nodes;
using Reflag.Internal;
using Xunit;
using static Reflag.Tests.FlagCollectionEvaluatorTests;

namespace Reflag.Tests;

public sealed class FlagDefinitionsJsonTests
{
    [Theory]
    [InlineData("flags")]
    [InlineData("segments")]
    [InlineData("flags/0/key")]
    [InlineData("flags/0/sourceVersionId")]
    [InlineData("flags/0/variants")]
    [InlineData("flags/0/rules")]
    [InlineData("flags/0/rules/0/id")]
    [InlineData("flags/0/rules/0/filter")]
    [InlineData("flags/0/rules/0/result")]
    [InlineData("flags/0/rules/0/filter/operator")]
    [InlineData("flags/0/rules/0/filter/filters")]
    [InlineData("flags/0/rules/0/filter/filters/0/value")]
    [InlineData("flags/0/rules/0/filter/filters/1/filter")]
    [InlineData("flags/0/rules/0/filter/filters/2/key")]
    [InlineData("flags/0/rules/0/filter/filters/2/partialRolloutAttribute")]
    [InlineData("flags/0/rules/0/filter/filters/2/partialRolloutThreshold")]
    [InlineData("flags/0/rules/0/filter/filters/3/field")]
    [InlineData("flags/0/rules/0/filter/filters/4/key")]
    [InlineData("flags/0/rules/0/filter/filters/4/variantKey")]
    [InlineData("flags/0/rules/0/filter/filters/5/key")]
    [InlineData("flags/0/rules/0/result/key")]
    [InlineData("flags/0/rules/0/result/attribute")]
    [InlineData("flags/0/rules/0/result/allocations")]
    [InlineData("flags/0/rules/0/result/allocations/0/percentage")]
    [InlineData("flags/0/rules/0/result/allocations/0/destination")]
    [InlineData("flags/0/rules/0/result/allocations/1/destination/type")]
    [InlineData("flags/0/rules/1/result/variantKey")]
    [InlineData("segments/0/key")]
    [InlineData("segments/0/filter")]
    public void Missing_required_definition_properties_are_rejected(string path)
    {
        var json = CollectionJson();
        Remove(json, path);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FlagDefinitions>(json.ToJsonString(), ReflagJson.Options));
    }

    [Theory]
    [InlineData("flags")]
    [InlineData("segments")]
    public void Incomplete_persisted_snapshots_are_rejected(string property)
    {
        var json = CollectionJson();
        json["schemaVersion"] = FlagsFallbackSnapshot.CurrentSchemaVersion;
        json["savedAt"] = DateTimeOffset.UtcNow;
        Assert.True(json.Remove(property));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FlagsFallbackSnapshot>(json.ToJsonString(), ReflagJson.Options));
    }

    [Fact]
    public void Optional_values_and_explicit_empty_groups_still_round_trip()
    {
        var json = CollectionJson();
        Remove(json, "flags/0/rules/0/filter/filters/3/values");
        json["segments"]![0]!["filter"] = JsonNode.Parse("{\"type\":\"group\",\"operator\":\"and\",\"filters\":[]}");

        var definitions = JsonSerializer.Deserialize<FlagDefinitions>(json.ToJsonString(), ReflagJson.Options)!;
        var prepared = PreparedFlagCollection.Prepare(definitions.Flags, definitions.Segments);
        var roundTrip = JsonSerializer.Deserialize<FlagDefinitions>(JsonSerializer.Serialize(prepared.Definitions, ReflagJson.Options), ReflagJson.Options)!;
        Assert.Equal(2, roundTrip.Flags.Count);
        Assert.Single(roundTrip.Segments);
    }

    private static JsonObject CollectionJson()
    {
        var filter = new FlagFilterGroupDefinition
        {
            Filters =
            [
                Yes,
                new FlagFilterNegationDefinition { Filter = Yes },
                new FlagPercentageRolloutFilterDefinition { Key = "split", PartialRolloutAttribute = "company.id", PartialRolloutThreshold = 50000 },
                new FlagContextFilterDefinition { Field = "company.plan", Operator = FlagContextFilterOperator.Is, Values = ["enterprise"] },
                FlagReference("b"),
                SegmentReference("enterprise"),
            ],
        };
        var json = JsonNode.Parse(TestDefinitions.FlagsResponse(1, [Flag("a", filter), Flag("b", Yes)], [Segment("enterprise", Yes)]))!.AsObject();
        json["flags"]![0]!["rules"]![0]!["result"] = JsonNode.Parse("""
            {"type":"percentageDistribution","key":"split","attribute":"company.id","allocations":[
                {"percentage":0,"destination":{"type":"nextRule"}},
                {"percentage":100,"destination":{"type":"variant","variantKey":"on"}}
            ]}
            """);
        return json;
    }

    private static void Remove(JsonNode json, string path)
    {
        var parts = path.Split('/');
        foreach (var part in parts.Take(parts.Length - 1))
        {
            json = (json is JsonArray array ? array[int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)] : json[part])!;
        }

        Assert.True(json.AsObject().Remove(parts[parts.Length - 1]));
    }
}
