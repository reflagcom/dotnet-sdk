using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class FlagEvaluationRuleEngineTests
{
    [Fact]
    public void EvaluateFlagRules_returns_first_matching_rule_and_preserves_rule_result_order()
    {
        var rules = new[]
        {
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagContextFilterDefinition
                    {
                        Field = "company.id",
                        Operator = FlagContextFilterOperator.Is,
                        Values = ["company-2"],
                    }),
                "first"),
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagContextFilterDefinition
                    {
                        Field = "company.id",
                        Operator = FlagContextFilterOperator.Is,
                        Values = ["company-1"],
                    }),
                "second"),
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagConstantFilterDefinition
                    {
                        Value = true,
                    }),
                "third"),
        };

        var result = FlagEvaluation.EvaluateFlagRules(
            "new-dashboard",
            rules,
            new Dictionary<string, object?>
            {
                ["company"] = new Dictionary<string, object?>
                {
                    ["id"] = "company-1",
                },
            });

        Assert.Equal("second", result.Value);
        Assert.Equal("rule #1 matched", result.Reason);
        Assert.Equal(new[] { false, true, true }, result.RuleEvaluationResults);
        Assert.Equivalent(
            new Dictionary<string, string>
            {
                ["company.id"] = "company-1",
            },
            result.Context);
        Assert.Empty(result.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_returns_null_for_reference_typed_values_when_no_rule_matches()
    {
        var rules = new[]
        {
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagContextFilterDefinition
                    {
                        Field = "user.id",
                        Operator = FlagContextFilterOperator.Is,
                        Values = ["user-1"],
                    }),
                "matched"),
        };

        var result = FlagEvaluation.EvaluateFlagRules(
            "beta-access",
            rules,
            new Dictionary<string, object?>());

        Assert.Null(result.Value);
        Assert.Equal("no matched rules", result.Reason);
        Assert.Equal(new[] { false }, result.RuleEvaluationResults);
        Assert.Equal(new[] { "user.id" }, result.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_deduplicates_missing_context_fields_and_preserves_first_seen_order()
    {
        var rules = new[]
        {
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagContextFilterDefinition
                    {
                        Field = "company.id",
                        Operator = FlagContextFilterOperator.Is,
                        Values = ["company-1"],
                    }),
                "first"),
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagContextFilterDefinition
                    {
                        Field = "user.id",
                        Operator = FlagContextFilterOperator.Is,
                        Values = ["user-1"],
                    }),
                "second"),
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagContextFilterDefinition
                    {
                        Field = "company.id",
                        Operator = FlagContextFilterOperator.Is,
                        Values = ["company-1"],
                    }),
                "third"),
        };

        var result = FlagEvaluation.EvaluateFlagRules("flag", rules, new Dictionary<string, object?>());

        Assert.Equal(new[] { "company.id", "user.id" }, result.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_short_circuits_and_group_before_later_missing_fields_are_visited()
    {
        var rules = new[]
        {
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagFilterGroupDefinition
                    {
                        Operator = "and",
                        Filters =
                        [
                            new FlagContextFilterDefinition
                            {
                                Field = "user.id",
                                Operator = FlagContextFilterOperator.Is,
                                Values = ["user-1"],
                            },
                            new FlagContextFilterDefinition
                            {
                                Field = "company.id",
                                Operator = FlagContextFilterOperator.Is,
                                Values = ["company-1"],
                            },
                        ],
                    }),
                "matched"),
        };

        var result = FlagEvaluation.EvaluateFlagRules("flag", rules, new Dictionary<string, object?>());

        Assert.Equal(new[] { "user.id" }, result.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_short_circuits_or_group_before_later_missing_fields_are_visited()
    {
        var rules = new[]
        {
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagFilterGroupDefinition
                    {
                        Operator = "or",
                        Filters =
                        [
                            new FlagConstantFilterDefinition
                            {
                                Value = true,
                            },
                            new FlagContextFilterDefinition
                            {
                                Field = "company.id",
                                Operator = FlagContextFilterOperator.Is,
                                Values = ["company-1"],
                            },
                        ],
                    }),
                "matched"),
        };

        var result = FlagEvaluation.EvaluateFlagRules("flag", rules, new Dictionary<string, object?>());

        Assert.Equal("matched", result.Value);
        Assert.Empty(result.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_supports_nested_group_and_negation_filters_and_preserves_missing_fields_from_evaluated_or_branches()
    {
        var rules = new[]
        {
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagFilterGroupDefinition
                    {
                        Operator = "and",
                        Filters =
                        [
                            new FlagContextFilterDefinition
                            {
                                Field = "company.id",
                                Operator = FlagContextFilterOperator.AnyOf,
                                Values = ["company-1", "company-2"],
                            },
                            new FlagFilterGroupDefinition
                            {
                                Operator = "or",
                                Filters =
                                [
                                    new FlagContextFilterDefinition
                                    {
                                        Field = "user.role",
                                        Operator = FlagContextFilterOperator.Is,
                                        Values = ["admin"],
                                    },
                                    new FlagFilterNegationDefinition
                                    {
                                        Filter = new FlagContextFilterDefinition
                                        {
                                            Field = "user.blocked",
                                            Operator = FlagContextFilterOperator.IsTrue,
                                        },
                                    },
                                ],
                            },
                        ],
                    }),
                "matched"),
        };

        var result = FlagEvaluation.EvaluateFlagRules(
            "flag",
            rules,
            new Dictionary<string, object?>
            {
                ["company"] = new Dictionary<string, object?>
                {
                    ["id"] = "company-2",
                },
                ["user"] = new Dictionary<string, object?>
                {
                    ["blocked"] = false,
                },
            });

        Assert.Equal("matched", result.Value);
        Assert.Equal(new[] { true }, result.RuleEvaluationResults);
        Assert.Equal(new[] { "user.role" }, result.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_handles_rollout_threshold_extremes_without_reporting_missing_fields_when_attribute_is_present()
    {
        var context = new Dictionary<string, object?>
        {
            ["company"] = new Dictionary<string, object?>
            {
                ["id"] = "company-1",
            },
        };

        var zeroThreshold = FlagEvaluation.EvaluateFlagRules(
            "flag-zero",
            [
                new EvaluationRule<string>(
                    FlagEvaluation.CompileFilter(
                        new FlagPercentageRolloutFilterDefinition
                        {
                            Key = "flag-zero",
                            PartialRolloutAttribute = "company.id",
                            PartialRolloutThreshold = 0,
                        }),
                    "matched"),
            ],
            context);

        var fullThreshold = FlagEvaluation.EvaluateFlagRules(
            "flag-full",
            [
                new EvaluationRule<string>(
                    FlagEvaluation.CompileFilter(
                        new FlagPercentageRolloutFilterDefinition
                        {
                            Key = "flag-full",
                            PartialRolloutAttribute = "company.id",
                            PartialRolloutThreshold = 100_000,
                        }),
                    "matched"),
            ],
            context);

        Assert.Null(zeroThreshold.Value);
        Assert.Empty(zeroThreshold.MissingContextFields);
        Assert.Equal("matched", fullThreshold.Value);
        Assert.Empty(fullThreshold.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_reports_missing_rollout_attribute()
    {
        var result = FlagEvaluation.EvaluateFlagRules(
            "flag",
            [
                new EvaluationRule<string>(
                    FlagEvaluation.CompileFilter(
                        new FlagPercentageRolloutFilterDefinition
                        {
                            Key = "flag",
                            PartialRolloutAttribute = "company.id",
                            PartialRolloutThreshold = 50_000,
                        }),
                    "matched"),
            ],
            new Dictionary<string, object?>());

        Assert.Null(result.Value);
        Assert.Equal(new[] { "company.id" }, result.MissingContextFields);
    }

    [Fact]
    public void EvaluateFlagRules_uses_and_group_empty_filter_list_as_true_and_or_group_empty_filter_list_as_false()
    {
        var result = FlagEvaluation.EvaluateFlagRules(
            "flag",
            [
                new EvaluationRule<string>(
                    FlagEvaluation.CompileFilter(
                        new FlagFilterGroupDefinition
                        {
                            Operator = "and",
                            Filters = Array.Empty<FlagFilterDefinition>(),
                        }),
                    "and-match"),
                new EvaluationRule<string>(
                    FlagEvaluation.CompileFilter(
                        new FlagFilterGroupDefinition
                        {
                            Operator = "or",
                            Filters = Array.Empty<FlagFilterDefinition>(),
                        }),
                    "or-match"),
            ],
            new Dictionary<string, object?>());

        Assert.Equal("and-match", result.Value);
        Assert.Equal(new[] { true, false }, result.RuleEvaluationResults);
    }

    [Fact]
    public void NewEvaluator_is_semantically_equivalent_to_EvaluateFlagRules()
    {
        var rules = new[]
        {
            new EvaluationRule<string>(
                FlagEvaluation.CompileFilter(
                    new FlagFilterGroupDefinition
                    {
                        Operator = "and",
                        Filters =
                        [
                            new FlagContextFilterDefinition
                            {
                                Field = "company.id",
                                Operator = FlagContextFilterOperator.AnyOf,
                                Values = ["company-1", "company-2"],
                            },
                            new FlagFilterNegationDefinition
                            {
                                Filter = new FlagContextFilterDefinition
                                {
                                    Field = "user.blocked",
                                    Operator = FlagContextFilterOperator.IsTrue,
                                },
                            },
                            new FlagFilterGroupDefinition
                            {
                                Operator = "or",
                                Filters =
                                [
                                    new FlagContextFilterDefinition
                                    {
                                        Field = "user.role",
                                        Operator = FlagContextFilterOperator.Is,
                                        Values = ["admin"],
                                    },
                                    new FlagContextFilterDefinition
                                    {
                                        Field = "user.role",
                                        Operator = FlagContextFilterOperator.Is,
                                        Values = ["owner"],
                                    },
                                ],
                            },
                        ],
                    }),
                "matched"),
        };

        var context = new Dictionary<string, object?>
        {
            ["company"] = new Dictionary<string, object?>
            {
                ["id"] = "company-2",
            },
            ["user"] = new Dictionary<string, object?>
            {
                ["blocked"] = false,
                ["role"] = "owner",
            },
        };

        var expected = FlagEvaluation.EvaluateFlagRules("flag", rules, context);
        var evaluator = FlagEvaluation.NewEvaluator(rules);
        var actual = evaluator(context, "flag");

        Assert.Equal(expected.FlagKey, actual.FlagKey);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.RuleEvaluationResults, actual.RuleEvaluationResults);
        Assert.Equal(expected.MissingContextFields, actual.MissingContextFields);
        Assert.Equivalent(expected.Context, actual.Context);
    }
}
