using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class FlagEvaluationOperatorsTests
{
    private static readonly DateTimeOffset FixedNow = new(2024, 1, 10, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object?[]> EqualityAndMembershipCases()
    {
        yield return ["value", FlagContextFilterOperator.Is, new[] { "value" }, true];
        yield return ["value", FlagContextFilterOperator.Is, new[] { "wrong" }, false];
        yield return ["value", FlagContextFilterOperator.IsNot, new[] { "wrong" }, true];
        yield return ["value", FlagContextFilterOperator.IsNot, new[] { "value" }, false];
        yield return ["value", FlagContextFilterOperator.AnyOf, new[] { "value", "other" }, true];
        yield return ["value", FlagContextFilterOperator.AnyOf, new[] { "other", "else" }, false];
        yield return ["value", FlagContextFilterOperator.NotAnyOf, new[] { "other", "else" }, true];
        yield return ["value", FlagContextFilterOperator.NotAnyOf, new[] { "value", "else" }, false];
        yield return ["start VALUE end", FlagContextFilterOperator.Contains, new[] { "value" }, true];
        yield return ["start VALUE end", FlagContextFilterOperator.NotContains, new[] { "value" }, false];
        yield return ["start end", FlagContextFilterOperator.NotContains, new[] { "value" }, true];
        yield return ["true", FlagContextFilterOperator.IsTrue, Array.Empty<string>(), true];
        yield return ["false", FlagContextFilterOperator.IsFalse, Array.Empty<string>(), true];
        yield return ["anything", FlagContextFilterOperator.IsTrue, Array.Empty<string>(), false];
        yield return ["value", FlagContextFilterOperator.Set, Array.Empty<string>(), true];
        yield return ["", FlagContextFilterOperator.Set, Array.Empty<string>(), false];
        yield return ["", FlagContextFilterOperator.NotSet, Array.Empty<string>(), true];
        yield return ["value", FlagContextFilterOperator.NotSet, Array.Empty<string>(), false];
    }

    public static IEnumerable<object?[]> NumericCases()
    {
        yield return ["1", FlagContextFilterOperator.Gt, "0", true];
        yield return ["2", FlagContextFilterOperator.Gt, "10", false];
        yield return ["2", FlagContextFilterOperator.Lt, "10", true];
        yield return ["10", FlagContextFilterOperator.Lt, "2", false];
        yield return ["value", FlagContextFilterOperator.Gt, "0", false];
        yield return ["1", FlagContextFilterOperator.Lt, "value", false];
    }

    public static IEnumerable<object?[]> RelativeDateCases()
    {
        yield return ["2024-01-15T00:00:00Z", FlagContextFilterOperator.After, "5", true];
        yield return ["2024-01-05T00:00:00Z", FlagContextFilterOperator.After, "5", false];
        yield return ["2024-01-04T23:59:59Z", FlagContextFilterOperator.Before, "5", true];
        yield return ["2024-01-05T00:00:00Z", FlagContextFilterOperator.Before, "5", false];
        yield return ["invalid-date", FlagContextFilterOperator.After, "5", false];
    }

    public static IEnumerable<object?[]> AbsoluteDateCases()
    {
        yield return ["2024-01-15T00:00:00Z", FlagContextFilterOperator.DateAfter, "2024-01-10T00:00:00Z", true];
        yield return ["2024-01-10T00:00:00Z", FlagContextFilterOperator.DateAfter, "2024-01-10T00:00:00Z", true];
        yield return ["2024-01-09T23:59:59Z", FlagContextFilterOperator.DateAfter, "2024-01-10T00:00:00Z", false];
        yield return ["2024-01-05T00:00:00Z", FlagContextFilterOperator.DateBefore, "2024-01-10T00:00:00Z", true];
        yield return ["2024-01-10T00:00:00Z", FlagContextFilterOperator.DateBefore, "2024-01-10T00:00:00Z", true];
        yield return ["2024-01-10T00:00:01Z", FlagContextFilterOperator.DateBefore, "2024-01-10T00:00:00Z", false];
        yield return ["invalid-date", FlagContextFilterOperator.DateAfter, "2024-01-10T00:00:00Z", false];
        yield return ["2024-01-10T00:00:00Z", FlagContextFilterOperator.DateBefore, "invalid-date", false];
    }

    public static IEnumerable<object?[]> HashVectors()
    {
        yield return ["EEuoT8KShb", 38026];
        yield return ["h7BOkvks5W", 81440];
        yield return ["IZeSn3LCfJ", 80149];
        yield return ["jxYGR0k2eG", 70348];
        yield return ["VnaiKHgo1E", 82432];
        yield return ["I3R27J9tGN", 88564];
        yield return ["JoCeRRF5wm", 67104];
        yield return ["D9yQyxGKlc", 90226];
        yield return ["gvfTO4h4Je", 98400];
        yield return ["zF5iPhvJuw", 53236];
    }

    [Theory]
    [MemberData(nameof(EqualityAndMembershipCases))]
    public void Evaluate_handles_equality_membership_boolean_and_presence_operators(
        string fieldValue,
        FlagContextFilterOperator @operator,
        string[] values,
        bool expected)
    {
        var result = FlagEvaluation.Evaluate(fieldValue, @operator, values, null, FixedNow);

        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(NumericCases))]
    public void Evaluate_handles_numeric_operators(
        string fieldValue,
        FlagContextFilterOperator @operator,
        string comparisonValue,
        bool expected)
    {
        var result = FlagEvaluation.Evaluate(fieldValue, @operator, [comparisonValue], null, FixedNow);

        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(RelativeDateCases))]
    public void Evaluate_handles_relative_date_operators_with_strict_comparison(
        string fieldValue,
        FlagContextFilterOperator @operator,
        string comparisonValue,
        bool expected)
    {
        var result = FlagEvaluation.Evaluate(fieldValue, @operator, [comparisonValue], null, FixedNow);

        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(AbsoluteDateCases))]
    public void Evaluate_handles_absolute_date_operators_with_inclusive_comparison(
        string fieldValue,
        FlagContextFilterOperator @operator,
        string comparisonValue,
        bool expected)
    {
        var result = FlagEvaluation.Evaluate(fieldValue, @operator, [comparisonValue], null, FixedNow);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Evaluate_supports_precomputed_sets_for_any_of_and_not_any_of()
    {
        var values = new[] { "company-1", "company-2" };
        var valueSet = new HashSet<string>(values, StringComparer.Ordinal);

        Assert.True(FlagEvaluation.Evaluate("company-2", FlagContextFilterOperator.AnyOf, values, valueSet, FixedNow));
        Assert.True(FlagEvaluation.Evaluate("company-3", FlagContextFilterOperator.NotAnyOf, values, valueSet, FixedNow));
        Assert.False(FlagEvaluation.Evaluate("company-3", FlagContextFilterOperator.AnyOf, values, valueSet, FixedNow));
        Assert.False(FlagEvaluation.Evaluate("company-1", FlagContextFilterOperator.NotAnyOf, values, valueSet, FixedNow));
    }

    [Theory]
    [MemberData(nameof(HashVectors))]
    public void HashInt_matches_golden_vectors(string input, int expected)
    {
        Assert.Equal(expected, Hashing.HashInt(input));
    }
}
