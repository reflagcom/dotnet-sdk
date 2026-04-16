using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class FlagEvaluationTests
{
    [Fact]
    public void FlattenJson_handles_arrays_nulls_and_empty_objects()
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

        Assert.Equal("u1", flattened["user.id"]);
        Assert.Equal("a", flattened["user.tags.0"]);
        Assert.Equal(string.Empty, flattened["user.tags.1"]);
        Assert.Equal("c", flattened["user.tags.2"]);
        Assert.Equal(string.Empty, flattened["company"]);
        Assert.Equal(string.Empty, flattened["other"]);
    }

    [Fact]
    public void EvaluateFlagRules_tracks_missing_context_fields()
    {
        var rules = new[]
        {
            new EvaluationRule<bool>(
                FlagEvaluation.CompileFilter(
                    new FlagFilterGroupDefinition
                    {
                        Operator = "and",
                        Filters =
                        [
                            new FlagContextFilterDefinition
                            {
                                Field = "company.id",
                                Operator = FlagContextFilterOperator.Is,
                                Values = ["company-1"],
                            },
                            new FlagPercentageRolloutFilterDefinition
                            {
                                Key = "new-dashboard",
                                PartialRolloutAttribute = "user.id",
                                PartialRolloutThreshold = 100_000,
                            },
                        ],
                    }),
                true),
        };

        var result = FlagEvaluation.EvaluateFlagRules(
            "new-dashboard",
            rules,
            new Dictionary<string, object?>());

        Assert.False(result.Value);
        Assert.Equal("no matched rules", result.Reason);
        Assert.Equal(new[] { false }, result.RuleEvaluationResults);
        Assert.Equal(new[] { "company.id" }, result.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_does_not_report_missing_fields_for_not_set()
    {
        var rules = new[]
        {
            new EvaluationRule<bool>(
                FlagEvaluation.CompileFilter(
                    new FlagContextFilterDefinition
                    {
                        Field = "user.email",
                        Operator = FlagContextFilterOperator.NotSet,
                    }),
                true),
        };

        var result = FlagEvaluation.EvaluateFlagRules(
            "beta-access",
            rules,
            new Dictionary<string, object?>());

        Assert.True(result.Value);
        Assert.Empty(result.MissingContextFields);
        Assert.Equal(new[] { true }, result.RuleEvaluationResults);
    }

    [Fact]
    public void HashInt_matches_known_vector()
    {
        Assert.Equal(38026, Hashing.HashInt("EEuoT8KShb"));
    }
}
