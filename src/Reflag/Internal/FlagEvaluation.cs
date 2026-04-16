using System.Collections;
using System.Globalization;

namespace Reflag.Internal;

internal sealed class EvaluationResult<T>
{
    public string FlagKey { get; init; } = string.Empty;

    public T? Value { get; init; }

    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<bool> RuleEvaluationResults { get; init; } = Array.Empty<bool>();

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> MissingContextFields { get; init; } = Array.Empty<string>();
}

internal sealed record EvaluationRule<T>(CompiledFilter Filter, T Value);

internal abstract class CompiledFilter
{
    public abstract bool Evaluate(
        IReadOnlyDictionary<string, string> context,
        OrderedStringSet missingContextFields,
        DateTimeOffset now);
}

internal sealed class CompiledConstantFilter(bool value) : CompiledFilter
{
    public override bool Evaluate(
        IReadOnlyDictionary<string, string> context,
        OrderedStringSet missingContextFields,
        DateTimeOffset now)
    {
        return value;
    }
}

internal sealed class CompiledNegationFilter(CompiledFilter filter) : CompiledFilter
{
    public override bool Evaluate(
        IReadOnlyDictionary<string, string> context,
        OrderedStringSet missingContextFields,
        DateTimeOffset now)
    {
        return !filter.Evaluate(context, missingContextFields, now);
    }
}

internal sealed class CompiledGroupFilter(string groupOperator, IReadOnlyList<CompiledFilter> filters) : CompiledFilter
{
    public override bool Evaluate(
        IReadOnlyDictionary<string, string> context,
        OrderedStringSet missingContextFields,
        DateTimeOffset now)
    {
        if (string.Equals(groupOperator, "and", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var filter in filters)
            {
                if (!filter.Evaluate(context, missingContextFields, now))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var filter in filters)
        {
            if (filter.Evaluate(context, missingContextFields, now))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class CompiledContextFilter(
    string field,
    FlagContextFilterOperator @operator,
    IReadOnlyList<string> values,
    HashSet<string>? valueSet) : CompiledFilter
{
    public override bool Evaluate(
        IReadOnlyDictionary<string, string> context,
        OrderedStringSet missingContextFields,
        DateTimeOffset now)
    {
        if (!context.TryGetValue(field, out var fieldValue))
        {
            if (@operator is FlagContextFilterOperator.Set or FlagContextFilterOperator.NotSet)
            {
                fieldValue = string.Empty;
            }
            else
            {
                missingContextFields.Add(field);
                return false;
            }
        }

        return FlagEvaluation.Evaluate(fieldValue, @operator, values, valueSet, now);
    }
}

internal sealed class CompiledRolloutPercentageFilter(
    string key,
    string partialRolloutAttribute,
    int partialRolloutThreshold) : CompiledFilter
{
    public override bool Evaluate(
        IReadOnlyDictionary<string, string> context,
        OrderedStringSet missingContextFields,
        DateTimeOffset now)
    {
        if (!context.TryGetValue(partialRolloutAttribute, out var attributeValue))
        {
            missingContextFields.Add(partialRolloutAttribute);
            return false;
        }

        return Hashing.HashInt($"{key}.{attributeValue}") < partialRolloutThreshold;
    }
}

internal sealed class OrderedStringSet
{
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly List<string> _values = new();

    public void Add(string value)
    {
        if (_set.Add(value))
        {
            _values.Add(value);
        }
    }

    public IReadOnlyList<string> ToList()
    {
        return _values.ToArray();
    }
}

internal static class FlagEvaluation
{
    public static IReadOnlyDictionary<string, string> FlattenJson(object? data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data is null)
        {
            return result;
        }

        Recurse(data, string.Empty, result);
        return result;
    }

    public static IReadOnlyDictionary<string, object?> UnflattenJson(IReadOnlyDictionary<string, object?> data)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            var parts = key.Split('.');
            IDictionary<string, object?> current = result;

            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                if (index == parts.Length - 1)
                {
                    current[part] = value;
                    break;
                }

                if (current.TryGetValue(part, out var existing) && existing is not IDictionary<string, object?> nested)
                {
                    break;
                }

                if (!current.TryGetValue(part, out var next) || next is not IDictionary<string, object?> nextDictionary)
                {
                    nextDictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                    current[part] = nextDictionary;
                }

                current = nextDictionary;
            }
        }

        return result;
    }

    public static bool Evaluate(
        string fieldValue,
        FlagContextFilterOperator @operator,
        IReadOnlyList<string> values,
        HashSet<string>? valueSet,
        DateTimeOffset now)
    {
        var comparisonValue = values.Count > 0 ? values[0] : string.Empty;

        return @operator switch
        {
            FlagContextFilterOperator.Is => fieldValue == comparisonValue,
            FlagContextFilterOperator.IsNot => fieldValue != comparisonValue,
            FlagContextFilterOperator.AnyOf => valueSet?.Contains(fieldValue) ?? values.Contains(fieldValue),
            FlagContextFilterOperator.NotAnyOf => !(valueSet?.Contains(fieldValue) ?? values.Contains(fieldValue)),
            FlagContextFilterOperator.Contains => ContainsIgnoreCase(fieldValue, comparisonValue),
            FlagContextFilterOperator.NotContains => !ContainsIgnoreCase(fieldValue, comparisonValue),
            FlagContextFilterOperator.Gt => CompareNumbers(fieldValue, comparisonValue, greaterThan: true),
            FlagContextFilterOperator.Lt => CompareNumbers(fieldValue, comparisonValue, greaterThan: false),
            FlagContextFilterOperator.After => CompareRelativeDate(fieldValue, comparisonValue, now, after: true),
            FlagContextFilterOperator.Before => CompareRelativeDate(fieldValue, comparisonValue, now, after: false),
            FlagContextFilterOperator.DateAfter => CompareAbsoluteDate(fieldValue, comparisonValue, after: true),
            FlagContextFilterOperator.DateBefore => CompareAbsoluteDate(fieldValue, comparisonValue, after: false),
            FlagContextFilterOperator.Set => fieldValue != string.Empty,
            FlagContextFilterOperator.NotSet => fieldValue == string.Empty,
            FlagContextFilterOperator.IsTrue => fieldValue == "true",
            FlagContextFilterOperator.IsFalse => fieldValue == "false",
            _ => false,
        };
    }

    public static EvaluationResult<T> EvaluateFlagRules<T>(
        string flagKey,
        IReadOnlyList<EvaluationRule<T>> rules,
        IReadOnlyDictionary<string, object?> context)
    {
        var flattenedContext = FlattenJson(context);
        var missingContextFields = new OrderedStringSet();
        var now = DateTimeOffset.UtcNow;

        var ruleEvaluationResults = new bool[rules.Count];
        for (var index = 0; index < rules.Count; index++)
        {
            ruleEvaluationResults[index] = rules[index].Filter.Evaluate(flattenedContext, missingContextFields, now);
        }

        var firstMatchIndex = Array.FindIndex(ruleEvaluationResults, static value => value);
        var matchedRule = firstMatchIndex >= 0 ? rules[firstMatchIndex] : null;

        return new EvaluationResult<T>
        {
            FlagKey = flagKey,
            Value = matchedRule is null ? default : matchedRule.Value,
            Context = flattenedContext,
            RuleEvaluationResults = ruleEvaluationResults,
            Reason = firstMatchIndex >= 0 ? $"rule #{firstMatchIndex} matched" : "no matched rules",
            MissingContextFields = missingContextFields.ToList(),
        };
    }

    public static Func<IReadOnlyDictionary<string, object?>, string, EvaluationResult<T>> NewEvaluator<T>(
        IReadOnlyList<EvaluationRule<T>> rules)
    {
        var translatedRules = rules
            .Select(rule => new EvaluationRule<T>(TranslateFilter(rule.Filter), rule.Value))
            .ToArray();

        return (context, flagKey) => EvaluateFlagRules(flagKey, translatedRules, context);
    }

    public static CompiledFilter CompileFilter(FlagFilterDefinition filter)
    {
        return filter switch
        {
            FlagConstantFilterDefinition constantFilter => new CompiledConstantFilter(constantFilter.Value),
            FlagFilterNegationDefinition negationFilter => new CompiledNegationFilter(CompileFilter(negationFilter.Filter)),
            FlagFilterGroupDefinition groupFilter => new CompiledGroupFilter(
                groupFilter.Operator,
                groupFilter.Filters.Select(CompileFilter).ToArray()),
            FlagContextFilterDefinition contextFilter => new CompiledContextFilter(
                contextFilter.Field,
                contextFilter.Operator,
                contextFilter.Values,
                contextFilter.Operator is FlagContextFilterOperator.AnyOf or FlagContextFilterOperator.NotAnyOf
                    ? new HashSet<string>(contextFilter.Values, StringComparer.Ordinal)
                    : null),
            FlagPercentageRolloutFilterDefinition rolloutFilter => new CompiledRolloutPercentageFilter(
                rolloutFilter.Key,
                rolloutFilter.PartialRolloutAttribute,
                rolloutFilter.PartialRolloutThreshold),
            _ => throw new ArgumentException($"Unknown filter type '{filter.GetType().FullName}'.", nameof(filter)),
        };
    }

    private static CompiledFilter TranslateFilter(CompiledFilter filter)
    {
        return filter;
    }

    private static bool ContainsIgnoreCase(string fieldValue, string comparisonValue)
    {
#if NETSTANDARD2_0
        return fieldValue.IndexOf(comparisonValue, StringComparison.OrdinalIgnoreCase) >= 0;
#else
        return fieldValue.Contains(comparisonValue, StringComparison.OrdinalIgnoreCase);
#endif
    }

    private static bool CompareNumbers(string fieldValue, string comparisonValue, bool greaterThan)
    {
        if (!double.TryParse(fieldValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var fieldNumber) ||
            !double.TryParse(comparisonValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var comparisonNumber) ||
            double.IsNaN(fieldNumber) ||
            double.IsNaN(comparisonNumber))
        {
            return false;
        }

        return greaterThan ? fieldNumber > comparisonNumber : fieldNumber < comparisonNumber;
    }

    private static bool CompareRelativeDate(string fieldValue, string comparisonValue, DateTimeOffset now, bool after)
    {
        if (!double.TryParse(comparisonValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var days))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(fieldValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fieldDate) &&
            !DateTimeOffset.TryParse(fieldValue, out fieldDate))
        {
            return false;
        }

        var daysAgo = now.AddDays(-days);
        return after ? fieldDate > daysAgo : fieldDate < daysAgo;
    }

    private static bool CompareAbsoluteDate(string fieldValue, string comparisonValue, bool after)
    {
        if (!DateTimeOffset.TryParse(fieldValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fieldDate) &&
            !DateTimeOffset.TryParse(fieldValue, out fieldDate))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(comparisonValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var comparisonDate) &&
            !DateTimeOffset.TryParse(comparisonValue, out comparisonDate))
        {
            return false;
        }

        return after ? fieldDate >= comparisonDate : fieldDate <= comparisonDate;
    }

    private static void Recurse(object? value, string path, IDictionary<string, string> result)
    {
        if (value is null)
        {
            if (path.Length > 0)
            {
                result[path] = string.Empty;
            }

            return;
        }

        if (ReflectionHelpers.IsScalarLike(value))
        {
            if (path.Length > 0)
            {
                result[path] = ReflectionHelpers.ConvertToFlatString(value);
            }

            return;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            RecurseDictionary(readOnlyDictionary, path, result);
            return;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            RecurseDictionary(dictionary, path, result);
            return;
        }

        if (value is IDictionary nonGenericDictionary)
        {
            var normalized = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in nonGenericDictionary)
            {
                if (entry.Key is null)
                {
                    continue;
                }

                normalized[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = entry.Value;
            }

            RecurseDictionary(normalized, path, result);
            return;
        }

        if (value is IEnumerable enumerable and not string)
        {
            var index = 0;
            var hadAny = false;
            foreach (var item in enumerable)
            {
                hadAny = true;
                Recurse(item, path.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : $"{path}.{index.ToString(CultureInfo.InvariantCulture)}", result);
                index++;
            }

            if (!hadAny && path.Length > 0)
            {
                result[path] = string.Empty;
            }

            return;
        }

        RecurseDictionary(
            ReflectionHelpers.EnumerateReadableProperties(value).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            path,
            result);
    }

    private static void RecurseDictionary(
        IEnumerable<KeyValuePair<string, object?>> values,
        string path,
        IDictionary<string, string> result)
    {
        var hadAny = false;
        foreach (var (key, nestedValue) in values)
        {
            hadAny = true;
            Recurse(nestedValue, path.Length == 0 ? key : $"{path}.{key}", result);
        }

        if (!hadAny && path.Length > 0)
        {
            result[path] = string.Empty;
        }
    }
}
