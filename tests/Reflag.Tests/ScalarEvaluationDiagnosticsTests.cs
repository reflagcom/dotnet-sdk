using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class ScalarEvaluationDiagnosticsTests
{
    public static IEnumerable<object[]> ScalarOperators()
    {
        yield return new object[] { FlagContextFilterOperator.Gt, "GT", "2", "1", "numeric", "numeric" };
        yield return new object[] { FlagContextFilterOperator.Lt, "LT", "1", "2", "numeric", "numeric" };
        yield return new object[] { FlagContextFilterOperator.After, "AFTER", "2024-01-10", "5", "a valid date", "a numeric day offset" };
        yield return new object[] { FlagContextFilterOperator.Before, "BEFORE", "2024-01-10", "5", "a valid date", "a numeric day offset" };
        yield return new object[] { FlagContextFilterOperator.DateAfter, "DATE_AFTER", "2024-01-10", "2024-01-01", "a valid date", "a valid date" };
        yield return new object[] { FlagContextFilterOperator.DateBefore, "DATE_BEFORE", "2024-01-01", "2024-01-10", "a valid date", "a valid date" };
    }

    [Theory]
    [MemberData(nameof(ScalarOperators))]
    public void Invalid_values_are_distinguished_without_exposing_values(
        FlagContextFilterOperator op, string wireOperator, string validContext, string validTargeting,
        string contextExpected, string targetingExpected)
    {
        foreach (var (invalidContext, invalidTargeting) in new[] { (true, false), (false, true), (true, true) })
        {
            var contextValue = invalidContext ? "private-context-value" : validContext;
            var targetingValue = invalidTargeting ? "private-targeting-value" : validTargeting;
            var rules = new[] { new EvaluationRule<bool>(FlagEvaluation.CompileFilter(Filter(op, targetingValue)), true) };
            var context = new Dictionary<string, object?> { ["value"] = contextValue };
            foreach (var result in new[]
            {
                FlagEvaluation.EvaluateFlagRules("flag", rules, context),
                FlagEvaluation.NewEvaluator(rules)(context, "flag"),
            })
            {
                Assert.False(result.Value);
                Assert.Equal(new[] { false }, result.RuleEvaluationResults);
                Assert.Empty(result.MissingContextFields);
                Assert.Equal((invalidContext ? 1 : 0) + (invalidTargeting ? 1 : 0), result.Errors.Count);
                foreach (var error in result.Errors)
                {
                    Assert.Equal("value", error.Field);
                    Assert.Equal(wireOperator, error.Operator);
                    Assert.DoesNotContain("private-context-value", error.Message);
                    Assert.DoesNotContain("private-targeting-value", error.Message);
                }

                if (invalidContext)
                {
                    Assert.Equal($"Context field \"value\" must be {contextExpected} for operator \"{wireOperator}\".", result.Errors[0].Message);
                    Assert.Equal("INVALID_CONTEXT_VALUE", result.Errors[0].Code);
                }

                if (invalidTargeting)
                {
                    Assert.Equal($"Targeting value for operator \"{wireOperator}\" and context field \"value\" must be {targetingExpected}.", result.Errors[^1].Message);
                    Assert.Equal("INVALID_TARGETING_VALUE", result.Errors[^1].Code);
                }
            }
        }
    }

    public static IEnumerable<object[]> ValidScalarOperators() =>
        ScalarOperators().Select(values => new[] { values[0], values[2], values[3] });

    [Theory]
    [MemberData(nameof(ValidScalarOperators))]
    public void Valid_values_do_not_produce_diagnostics(
        FlagContextFilterOperator op, string validContext, string validTargeting)
    {
        var result = Run(Filter(op, validTargeting), new Dictionary<string, object?> { ["value"] = validContext });
        Assert.Empty(result.Errors);
        Assert.Empty(result.MissingContextFields);
    }

    [Theory]
    [InlineData("Infinity", FlagContextFilterOperator.Gt, "1")]
    [InlineData("1", FlagContextFilterOperator.Lt, "Infinity")]
    public void Numeric_infinity_semantics_are_preserved(string contextValue, FlagContextFilterOperator op, string targetingValue)
    {
        var result = Run(Filter(op, targetingValue), new Dictionary<string, object?> { ["value"] = contextValue });
        Assert.True(result.Value);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e100")]
    public void Invalid_relative_date_offsets_do_not_throw(string offset)
    {
        var result = Run(Filter(FlagContextFilterOperator.After, offset), new Dictionary<string, object?> { ["value"] = "2024-01-10" });
        Assert.False(result.Value);
        Assert.Equal("INVALID_TARGETING_VALUE", Assert.Single(result.Errors).Code);
    }

    [Theory]
    [InlineData(FlagContextFilterOperator.Gt)]
    [InlineData(FlagContextFilterOperator.After)]
    [InlineData(FlagContextFilterOperator.DateAfter)]
    public void Missing_comparison_values_are_invalid_targeting(FlagContextFilterOperator op)
    {
        var result = Run(Filter(op), new Dictionary<string, object?> { ["value"] = op == FlagContextFilterOperator.Gt ? "2" : "2024-01-10" });
        Assert.False(result.Value);
        Assert.Equal("INVALID_TARGETING_VALUE", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Invalid_conditions_fail_negations_and_or_groups_closed_and_later_rules_can_match()
    {
        var invalid = Filter(FlagContextFilterOperator.Gt, "not-numeric");
        var rules = new[]
        {
            new EvaluationRule<bool>(FlagEvaluation.CompileFilter(new FlagFilterNegationDefinition { Filter = invalid }), true),
            new EvaluationRule<bool>(FlagEvaluation.CompileFilter(new FlagFilterGroupDefinition
            {
                Operator = "or",
                Filters = new FlagFilterDefinition[] { invalid, new FlagConstantFilterDefinition { Value = true } },
            }), true),
            new EvaluationRule<bool>(new CompiledConstantFilter(true), true),
        };
        var result = FlagEvaluation.EvaluateFlagRules("flag", rules, new Dictionary<string, object?> { ["value"] = "also-not-numeric" });
        Assert.True(result.Value);
        Assert.Equal(new[] { false, false, true }, result.RuleEvaluationResults);
        Assert.Equal(new[] { "INVALID_CONTEXT_VALUE", "INVALID_TARGETING_VALUE" }, result.Errors.Select(error => error.Code));
    }

    [Theory]
    [InlineData("\"FUTURE_OPERATOR\"", "FUTURE_OPERATOR")]
    [InlineData("\"\"", "")]
    [InlineData("null", "null")]
    [InlineData("123", "123")]
    [InlineData("true", "true")]
    [InlineData("{\"private\":\"value\"}", "[object Object]")]
    [InlineData("[\"private-value\"]", "[object Array]")]
    public void Unknown_runtime_operators_are_preserved_and_fail_closed(string jsonOperator, string expectedOperator)
    {
        var json = "{\"type\":\"context\",\"field\":\"value\",\"operator\":" + jsonOperator + ",\"values\":[\"admin\"]}";
        var filter = JsonSerializer.Deserialize<FlagContextFilterDefinition>(json)!;
        Assert.Equal(FlagContextFilterOperator.Unknown, filter.Operator);
        using var roundTrip = JsonDocument.Parse(JsonSerializer.Serialize<FlagFilterDefinition>(filter));
        Assert.Equal(expectedOperator, roundTrip.RootElement.GetProperty("operator").GetString());

        foreach (var context in new[]
        {
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["value"] = "admin" },
            new Dictionary<string, object?> { ["value"] = new[] { "admin" } },
        })
        {
            var result = Run(new FlagFilterNegationDefinition { Filter = filter }, context);
            Assert.False(result.Value);
            Assert.Empty(result.MissingContextFields);
            var error = Assert.Single(result.Errors);
            Assert.Equal("UNKNOWN_OPERATOR", error.Code);
            Assert.Equal(expectedOperator, error.Operator);
            Assert.Equal($"Unknown targeting operator \"{expectedOperator}\" for context field \"value\".", error.Message);
        }
    }

    [Fact]
    public void Missing_and_invalid_enum_operators_produce_unknown_operator_diagnostics()
    {
        var missing = JsonSerializer.Deserialize<FlagContextFilterDefinition>("{\"type\":\"context\",\"field\":\"value\"}")!;
        Assert.Equal("undefined", Assert.Single(Run(missing, new Dictionary<string, object?>()).Errors).Operator);
        var invalid = Run(Filter((FlagContextFilterOperator)123), new Dictionary<string, object?>());
        Assert.Equal("123", Assert.Single(invalid.Errors).Operator);
        Assert.Equal("UNKNOWN_OPERATOR", invalid.Errors[0].Code);
    }

    [Fact]
    public void Unknown_operator_names_survive_fallback_snapshot_round_trips()
    {
        var filter = JsonSerializer.Deserialize<FlagContextFilterDefinition>("{\"type\":\"context\",\"field\":\"value\",\"operator\":\"FUTURE_OPERATOR\"}")!;
        var snapshot = new FlagsFallbackSnapshot
        {
            SchemaVersion = 1,
            Flags = new[] { TestDefinitions.CreateFlag("flag", 1, filter) },
        };
        var restored = JsonSerializer.Deserialize<FlagsFallbackSnapshot>(JsonSerializer.Serialize(snapshot))!;
        var result = Run(restored.Flags[0].Targeting.Rules[0].Filter, new Dictionary<string, object?>());
        Assert.Equal("FUTURE_OPERATOR", Assert.Single(result.Errors).Operator);
        Assert.Equal("UNKNOWN_OPERATOR", result.Errors[0].Code);
    }

    [Fact]
    public void Known_operator_wire_names_round_trip()
    {
        foreach (var op in Enum.GetValues<FlagContextFilterOperator>().Where(op => op != FlagContextFilterOperator.Unknown))
        {
            var json = JsonSerializer.Serialize(Filter(op));
            Assert.Equal(op, JsonSerializer.Deserialize<FlagContextFilterDefinition>(json)!.Operator);
        }
    }

    private static FlagContextFilterDefinition Filter(FlagContextFilterOperator op, params string[] values) => new()
    {
        Field = "value",
        Operator = op,
        Values = values,
    };

    private static EvaluationResult<bool> Run(FlagFilterDefinition filter, IReadOnlyDictionary<string, object?> context) =>
        FlagEvaluation.EvaluateFlagRules("flag", new[] { new EvaluationRule<bool>(FlagEvaluation.CompileFilter(filter), true) }, context);
}
