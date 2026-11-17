using System.Text.Json;
using Reflag.Internal;
using Xunit;
using static Reflag.Tests.FlagCollectionEvaluatorTests;

namespace Reflag.Tests;

public sealed class DependencyMemoizationTests
{
    [Theory]
    [InlineData(false, "match")]
    [InlineData(false, "nonmatch")]
    [InlineData(false, "missing")]
    [InlineData(false, "invalid")]
    [InlineData(true, "match")]
    [InlineData(true, "nonmatch")]
    [InlineData(true, "missing")]
    [InlineData(true, "invalid")]
    public void Shared_dependencies_have_bounded_work_and_no_cross_call_cache(bool flagDependencies, string outcome)
    {
        var leaf = new FlagContextFilterDefinition
        {
            Field = "plan",
            Operator = outcome == "invalid" ? FlagContextFilterOperator.Gt : FlagContextFilterOperator.Is,
            Values = [outcome == "invalid" ? "100" : "enterprise"],
        };
        var flags = new List<FlagDefinition>();
        var segments = new List<SegmentDefinition>();
        var prefix = flagDependencies ? "f" : "s";
        FlagFilterDefinition Reference(string key) => flagDependencies ? FlagReference(key) : SegmentReference(key);
        void AddDependency(string key, FlagFilterDefinition filter)
        {
            if (flagDependencies)
            {
                flags.Add(Flag(key, filter));
            }
            else
            {
                segments.Add(Segment(key, filter));
            }
        }

        AddDependency($"{prefix}0", leaf);
        for (var index = 1; index <= 14; index++)
        {
            AddDependency($"{prefix}{index}", new FlagFilterGroupDefinition
            {
                // Force both references to be visited for matches AND nonmatches/errors.
                Operator = outcome == "match" ? "and" : "or",
                Filters = [Reference($"{prefix}{index - 1}"), Reference($"{prefix}{index - 1}")],
            });
        }

        flags.Add(Flag("root-a", Reference($"{prefix}14")));
        flags.Add(Flag("root-b", Reference($"{prefix}14")));
        var evaluator = new FlagCollectionEvaluator(flags, segments);
        var context = outcome == "missing"
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["plan"] = outcome == "match" ? "enterprise" : "free" };
        var oppositeContext = new Dictionary<string, object?>
        {
            ["plan"] = outcome == "match" ? "free" : outcome == "invalid" ? "200" : "enterprise",
        };

        var errorCode = outcome switch
        {
            "missing" => "MISSING_CONTEXT_FIELD",
            "invalid" => "INVALID_COMPARISON",
            _ => null,
        };
        foreach (var (currentContext, matched, expectedError) in new[]
        {
            (context, outcome == "match", errorCode),
            (oppositeContext, outcome != "match", (string?)null),
            (context, outcome == "match", errorCode),
        })
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var results = evaluator.EvaluateAll(currentContext);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            // A generous allocation bound, not a timing assertion: uncached diamonds allocate tens of MB.
            Assert.True(allocated < 1_000_000, $"Shared dependency evaluation allocated {allocated:N0} bytes.");

            foreach (var key in new[] { "root-a", "root-b" })
            {
                var result = results[key];
                Assert.Equal(matched, result.Value!.Value.GetBoolean());
                Assert.Equal(matched ? "on" : "off", result.VariantKey);
                Assert.Equal($"version-{key}", result.SourceVersionId);
                Assert.Equal(matched ? "rule-0" : "default", result.MatchedRuleId);
                if (expectedError is not null)
                {
                    var error = Assert.Single(result.Errors);
                    Assert.Equal(expectedError, error.Code);
                    Assert.Equal(flagDependencies ? "flag" : "segment", error.Source!.Type);
                    Assert.Equal($"{prefix}0", error.Source.Key);
                }
                else
                {
                    Assert.Empty(result.Errors);
                }

                allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var single = evaluator.EvaluateFlag(currentContext, key);
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                Assert.True(allocated < 1_000_000, $"Single-flag dependency evaluation allocated {allocated:N0} bytes.");
                Assert.Equal(JsonSerializer.Serialize(result, ReflagJson.Options), JsonSerializer.Serialize(single, ReflagJson.Options));
            }
        }
    }

    [Fact]
    public void Cached_unresolved_dependencies_cannot_grant_through_negation()
    {
        var unresolved = new FlagDefinition
        {
            Key = "unresolved",
            SourceVersionId = "v1",
            Variants = new Dictionary<string, JsonElement> { ["on"] = JsonSerializer.SerializeToElement(true) },
            Rules = [],
        };
        var evaluator = new FlagCollectionEvaluator(
            [unresolved, Flag("a", new FlagFilterNegationDefinition { Filter = FlagReference("unresolved") }),
                Flag("b", new FlagFilterNegationDefinition { Filter = FlagReference("unresolved") })],
            []);

        var results = evaluator.EvaluateAll(new Dictionary<string, object?>());
        Assert.False(results["unresolved"].Resolved);
        foreach (var key in new[] { "a", "b" })
        {
            Assert.False(results[key].Value!.Value.GetBoolean());
            var error = Assert.Single(results[key].Errors);
            Assert.Equal("INVALID_FLAG_DEFINITION", error.Code);
            Assert.Equal("flag", error.Source!.Type);
            Assert.Equal("unresolved", error.Source.Key);
        }
    }
}
