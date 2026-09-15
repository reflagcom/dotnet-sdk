using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class ArrayEvaluationTests
{
    [Theory]
    [InlineData(FlagContextFilterOperator.Contains, true)]
    [InlineData(FlagContextFilterOperator.NotContains, false)]
    [InlineData(FlagContextFilterOperator.AnyOf, true)]
    [InlineData(FlagContextFilterOperator.NotAnyOf, false)]
    [InlineData(FlagContextFilterOperator.Is, false)]
    [InlineData(FlagContextFilterOperator.IsNot, true)]
    [InlineData(FlagContextFilterOperator.Set, true)]
    [InlineData(FlagContextFilterOperator.NotSet, false)]
    public void Arrays_use_whole_value_membership(FlagContextFilterOperator op, bool expected)
    {
        var result = Evaluate(op, new[] { "admin", "editor" });
        Assert.Equal(expected, result.Value);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(FlagContextFilterOperator.Is, false)]
    [InlineData(FlagContextFilterOperator.IsNot, true)]
    [InlineData(FlagContextFilterOperator.Contains, false)]
    [InlineData(FlagContextFilterOperator.NotContains, true)]
    [InlineData(FlagContextFilterOperator.AnyOf, false)]
    [InlineData(FlagContextFilterOperator.NotAnyOf, true)]
    [InlineData(FlagContextFilterOperator.Set, false)]
    [InlineData(FlagContextFilterOperator.NotSet, true)]
    public void Empty_arrays_are_present_but_not_set(FlagContextFilterOperator op, bool expected)
    {
        var result = Evaluate(op, Array.Empty<string>());
        Assert.Equal(expected, result.Value);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Equality_requires_exactly_one_element_and_membership_is_case_sensitive()
    {
        Assert.True(Evaluate(FlagContextFilterOperator.Is, new[] { "admin" }).Value);
        Assert.False(Evaluate(FlagContextFilterOperator.Is, new[] { "admin", "admin" }).Value);
        Assert.True(Evaluate(FlagContextFilterOperator.IsNot, new[] { "admin", "admin" }).Value);
        Assert.False(Evaluate(FlagContextFilterOperator.Contains, new[] { "ADMIN", "administrator" }).Value);
        Assert.True(Evaluate(FlagContextFilterOperator.Contains, "ADMINISTRATOR").Value);
        Assert.False(Evaluate(FlagContextFilterOperator.AnyOf, "[\"admin\"]").Value);
    }

    [Fact]
    public void FlattenContext_normalizes_arrays_without_numeric_paths_or_changing_FlattenJson()
    {
        var context = new Dictionary<string, object?>
        {
            ["roles"] = new object?[] { "admin", 42, true, null, new { nested = "value" }, new[] { 1, 2 } },
        };
        var flat = FlagEvaluation.FlattenContext(context);
        Assert.Single(flat);
        Assert.Equal(new[] { "admin", "42", "true", "", "{\"nested\":\"value\"}", "[1,2]" }, Assert.IsType<string[]>(flat["roles"]));
        Assert.Equal("admin", FlagEvaluation.FlattenJson(context)["roles.0"]);
    }

    [Theory]
    [InlineData("café", "{\"name\":\"café\"}")]
    [InlineData("<tag>&'", "{\"name\":\"<tag>&'\"}")]
    [InlineData("say \"hello\"", "{\"name\":\"say \\\"hello\\\"\"}")]
    [InlineData("line\n\\end", "{\"name\":\"line\\n\\\\end\"}")]
    public void Composite_array_escaping_matches_JavaScript(string value, string expectedJson)
    {
        var context = ReflagContext.From(new { Other = new { items = new[] { new { name = value } } } });
        var result = Run(new FlagContextFilterDefinition
        {
            Field = "other.items",
            Operator = FlagContextFilterOperator.Contains,
            Values = [expectedJson],
        }, ReflagContextNormalizer.ToEvaluationObject(context));

        Assert.Equal(new[] { expectedJson }, Assert.IsType<string[]>(result.Context["other.items"]));
        Assert.True(result.Value);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(FlagContextFilterOperator.Gt, "GT")]
    [InlineData(FlagContextFilterOperator.Lt, "LT")]
    [InlineData(FlagContextFilterOperator.After, "AFTER")]
    [InlineData(FlagContextFilterOperator.Before, "BEFORE")]
    [InlineData(FlagContextFilterOperator.DateAfter, "DATE_AFTER")]
    [InlineData(FlagContextFilterOperator.DateBefore, "DATE_BEFORE")]
    [InlineData(FlagContextFilterOperator.IsTrue, "IS_TRUE")]
    [InlineData(FlagContextFilterOperator.IsFalse, "IS_FALSE")]
    public void Unsupported_operators_fail_closed_under_negation(FlagContextFilterOperator op, string wireOperator)
    {
        var filter = new FlagFilterNegationDefinition { Filter = ContextFilter(op) };
        var result = Run(filter, new Dictionary<string, object?> { ["roles"] = new[] { "admin" } });
        Assert.False(result.Value);
        Assert.Empty(result.MissingContextFields);
        var error = Assert.Single(result.Errors);
        Assert.Equal("UNSUPPORTED_ARRAY_OPERATOR", error.Code);
        Assert.Equal("roles", error.Field);
        Assert.Equal(wireOperator, error.Operator);
    }

    [Fact]
    public void Missing_fields_fail_closed_under_negation_and_diagnostics_are_deduplicated_across_rules()
    {
        var filter = FlagEvaluation.CompileFilter(new FlagFilterNegationDefinition { Filter = ContextFilter(FlagContextFilterOperator.Is) });
        var result = FlagEvaluation.EvaluateFlagRules("flag", new[]
        {
            new EvaluationRule<bool>(filter, true),
            new EvaluationRule<bool>(filter, true),
            new EvaluationRule<bool>(new CompiledConstantFilter(true), true),
        }, new Dictionary<string, object?>());
        Assert.True(result.Value);
        Assert.Equal(new[] { false, false, true }, result.RuleEvaluationResults);
        Assert.Equal(new[] { "roles" }, result.MissingContextFields);
        var error = Assert.Single(result.Errors);
        Assert.Equal("MISSING_CONTEXT_FIELD", error.Code);
        Assert.Null(error.Operator);
        Assert.False(JsonDocument.Parse(JsonSerializer.Serialize(error)).RootElement.TryGetProperty("operator", out _));
    }

    [Fact]
    public void Array_rollouts_are_rejected_even_at_full_threshold()
    {
        var result = Run(new FlagPercentageRolloutFilterDefinition
        {
            Key = "flag", PartialRolloutAttribute = "roles", PartialRolloutThreshold = 100000,
        }, new Dictionary<string, object?> { ["roles"] = new[] { "admin" } });
        Assert.False(result.Value);
        Assert.Equal("rolloutPercentage", Assert.Single(result.Errors).Operator);
    }

    private static FlagContextFilterDefinition ContextFilter(FlagContextFilterOperator op) => new()
    {
        Field = "roles", Operator = op, Values = ["admin"],
    };

    private static EvaluationResult<bool> Evaluate(FlagContextFilterOperator op, object value) =>
        Run(ContextFilter(op), new Dictionary<string, object?> { ["roles"] = value });

    private static EvaluationResult<bool> Run(FlagFilterDefinition filter, IReadOnlyDictionary<string, object?> context) =>
        FlagEvaluation.EvaluateFlagRules("flag", new[] { new EvaluationRule<bool>(FlagEvaluation.CompileFilter(filter), true) }, context);
}
