using System.Globalization;
using System.Text.Json;

namespace Reflag.Internal;

/// <summary>
/// Protocol-v2 collection evaluator, ported from <c>@reflag/flag-evaluation</c> 2.x.
/// Prepare once per definition refresh; evaluation resolves flag/segment references recursively
/// without caching context-dependent results.
/// Throws for duplicate keys, missing references, unknown referenced variants and cycles.
/// </summary>
internal sealed class FlagCollectionEvaluator
{
    private const string MissingContextField = "MISSING_CONTEXT_FIELD";
    private const string UnsupportedArrayOperator = "UNSUPPORTED_ARRAY_OPERATOR";
    private const string InvalidComparison = "INVALID_COMPARISON";
    private const string InvalidFlagDefinition = "INVALID_FLAG_DEFINITION";
    private const string InvalidKey = "invalid";

    private readonly Dictionary<string, PreparedFlag> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PreparedFilter> _segments = new(StringComparer.Ordinal);

    public FlagCollectionEvaluator(IEnumerable<FlagDefinition> flags, IEnumerable<SegmentDefinition> segments)
    {
        foreach (var flag in flags)
        {
            if (_flags.ContainsKey(flag.Key))
            {
                throw new ArgumentException($"Duplicate flag key \"{flag.Key}\"");
            }

            _flags[flag.Key] = new PreparedFlag(
                flag.Key,
                flag.SourceVersionId,
                CollectionHelpers.ToDictionary(flag.Variants, StringComparer.Ordinal),
                flag.Rules.Select(PrepareRule).ToArray());
        }

        foreach (var segment in segments)
        {
            if (_segments.ContainsKey(segment.Key))
            {
                throw new ArgumentException($"Duplicate segment key \"{segment.Key}\"");
            }

            _segments[segment.Key] = PrepareFilter(segment.Filter);
        }

        ValidateReferences();
    }

    public IEnumerable<string> FlagKeys => _flags.Keys;

    public bool HasFlag(string key) => _flags.ContainsKey(key);

    /// <summary>Evaluates every flag against one normalized context. Unresolved flags do not affect others.</summary>
    public Dictionary<string, RawReflagFlag> EvaluateAll(IReadOnlyDictionary<string, object?> context)
    {
        var state = new EvaluationState(ContextFlattening.FlattenContext(context), DateTimeOffset.UtcNow);
        var results = new Dictionary<string, RawReflagFlag>(StringComparer.Ordinal);
        foreach (var flag in _flags.Values)
        {
            results[flag.Key] = EvaluatePreparedFlag(flag, state);
        }

        return results;
    }

    /// <summary>Evaluates one flag and its dependencies, but not unrelated flags.</summary>
    public RawReflagFlag EvaluateFlag(IReadOnlyDictionary<string, object?> context, string flagKey)
    {
        if (!_flags.TryGetValue(flagKey, out var flag))
        {
            throw new KeyNotFoundException($"Unknown flag \"{flagKey}\"");
        }

        return EvaluatePreparedFlag(flag, new EvaluationState(ContextFlattening.FlattenContext(context), DateTimeOffset.UtcNow));
    }

    /// <summary>The original SHA-256 cohort hash, with an inclusive range of 0..100000.</summary>
    internal static int HashInt(string hashInput) => Hashing.HashInt(hashInput);

    private RawReflagFlag EvaluatePreparedFlag(PreparedFlag flag, EvaluationState state)
    {
        var errors = new ErrorSet();
        var filterErrors = new ErrorSet();
        var ruleResults = new List<ReflagRuleEvaluationResult>();
        foreach (var rule in flag.Rules)
        {
            filterErrors.Clear();
            var filterMatched = EvaluateFilter(rule.Filter, state, filterErrors, errors);
            errors.SetAll(filterErrors);
            var matched = filterMatched && filterErrors.Count == 0;
            if (!matched)
            {
                ruleResults.Add(new ReflagRuleEvaluationResult { RuleId = rule.Id, Matched = false });
                continue;
            }

            PreparedDestination destination;
            int? allocationIndex = null;
            if (rule.Distribution is { } distribution)
            {
                if (distribution.Error is not null)
                {
                    ruleResults.Add(new ReflagRuleEvaluationResult { RuleId = rule.Id, Matched = true });
                    SetInvalidDefinition(errors, distribution.Error);
                    break;
                }

                if (!state.Context.TryGetValue(distribution.Attribute, out var attributeValue))
                {
                    ruleResults.Add(new ReflagRuleEvaluationResult { RuleId = rule.Id, Matched = true });
                    SetMissingField(errors, distribution.Attribute);
                    continue;
                }

                if (attributeValue is string[])
                {
                    ruleResults.Add(new ReflagRuleEvaluationResult { RuleId = rule.Id, Matched = true });
                    SetUnsupportedArray(errors, distribution.Attribute, "percentageDistribution");
                    continue;
                }

                // Preserve every bucket except the inclusive endpoint; 0% stays empty.
                var bucket = Math.Min(Hashing.HashInt($"{distribution.Key}.{attributeValue}"), 99_999);
                var index = 0;
                while (index < distribution.Allocations.Length - 1 && bucket >= distribution.Allocations[index].UpperBound)
                {
                    index++;
                }

                allocationIndex = index;
                destination = distribution.Allocations[index].Destination;
            }
            else
            {
                destination = rule.Variant!;
            }

            ruleResults.Add(new ReflagRuleEvaluationResult
            {
                RuleId = rule.Id,
                Matched = true,
                AllocationIndex = allocationIndex,
                Destination = destination.ToDefinition(),
            });

            if (destination.VariantKey is null)
            {
                continue;
            }

            if (!flag.Variants.TryGetValue(destination.VariantKey, out var value))
            {
                SetInvalidDefinition(errors, $"Unknown variant {destination.VariantKey} in rule {rule.Id}");
                break;
            }

            return new RawReflagFlag
            {
                FlagKey = flag.Key,
                Resolved = true,
                Value = value,
                VariantKey = destination.VariantKey,
                SourceVersionId = flag.SourceVersionId,
                MatchedRuleId = rule.Id,
                AllocationIndex = allocationIndex,
                RuleResults = ruleResults,
                Errors = errors.ToArray(),
            };
        }

        if (!errors.Contains(InvalidKey))
        {
            SetInvalidDefinition(errors, "No rule selected a variant; a catch-all default is required");
        }

        return new RawReflagFlag
        {
            FlagKey = flag.Key,
            Resolved = false,
            SourceVersionId = flag.SourceVersionId,
            RuleResults = ruleResults,
            Errors = errors.ToArray(),
        };
    }

    private bool EvaluateFilter(PreparedFilter filter, EvaluationState state, ErrorSet errors, ErrorSet diagnostics)
    {
        switch (filter)
        {
            case ConstantFilter constant:
                return constant.Value;

            case ContextFilter context:
                if (!state.Context.TryGetValue(context.Field, out var fieldValue))
                {
                    if (context.Operator is not (FlagContextFilterOperator.Set or FlagContextFilterOperator.NotSet))
                    {
                        SetMissingField(errors, context.Field);
                        return false;
                    }

                    fieldValue = string.Empty;
                }

                return Compare(fieldValue, context, state.Now, errors);

            case RolloutFilter rollout:
                {
                    if (!state.Context.TryGetValue(rollout.Attribute, out var value))
                    {
                        SetMissingField(errors, rollout.Attribute);
                        return false;
                    }

                    if (value is string[])
                    {
                        SetUnsupportedArray(errors, rollout.Attribute, "rolloutPercentage");
                        return false;
                    }

                    return Hashing.HashInt($"{rollout.Key}.{value}") < rollout.Threshold;
                }

            case FlagReferenceFilter flagReference:
                {
                    // References were validated at preparation time, including unreachable branches.
                    var result = EvaluatePreparedFlag(_flags[flagReference.Key], state);
                    var matched = result.Resolved && result.VariantKey == flagReference.VariantKey;
                    // A valid selection wins over diagnostics in independent sibling rules.
                    // An uncertain nonmatch must not become a grant through negation.
                    PropagateErrors(result.Errors, "flag", flagReference.Key, diagnostics, matched ? null : errors);
                    return matched;
                }

            case SegmentReferenceFilter segmentReference:
                {
                    var segmentErrors = new ErrorSet();
                    var segmentDiagnostics = new ErrorSet();
                    var matched = EvaluateFilter(_segments[segmentReference.Key], state, segmentErrors, segmentDiagnostics);
                    PropagateErrors(segmentDiagnostics.Values, "segment", segmentReference.Key, diagnostics, null);
                    // Segments are predicates, not sets of independent enable rules.
                    PropagateErrors(segmentErrors.Values, "segment", segmentReference.Key, diagnostics, errors);
                    return matched && segmentErrors.Count == 0;
                }

            case GroupFilter group:
                foreach (var child in group.Filters)
                {
                    var matched = EvaluateFilter(child, state, errors, diagnostics);
                    if (group.IsAnd && !matched)
                    {
                        return false;
                    }

                    if (!group.IsAnd && matched)
                    {
                        return true;
                    }
                }

                return group.IsAnd;

            case NegationFilter negation:
                return !EvaluateFilter(negation.Filter, state, errors, diagnostics);

            default:
                SetInvalidDefinition(errors, "Unsupported filter type");
                return false;
        }
    }

    private static bool Compare(object? fieldValue, ContextFilter filter, DateTimeOffset now, ErrorSet errors)
    {
        var @operator = filter.Operator;
        var value = filter.Values.Length > 0 ? filter.Values[0] : null;
        if (@operator is FlagContextFilterOperator.AnyOf or FlagContextFilterOperator.NotAnyOf)
        {
            var matches = fieldValue is string[] candidates
                ? candidates.Any(filter.ValueSet!.Contains)
                : filter.ValueSet!.Contains((string)fieldValue!);
            return @operator == FlagContextFilterOperator.AnyOf ? matches : !matches;
        }

        if (fieldValue is string[] array)
        {
            switch (@operator)
            {
                case FlagContextFilterOperator.Is:
                    return value is not null && array.Length == 1 && array[0] == value;
                case FlagContextFilterOperator.IsNot:
                    return value is not null && !(array.Length == 1 && array[0] == value);
                case FlagContextFilterOperator.Contains:
                    return value is not null && array.Contains(value, StringComparer.Ordinal);
                case FlagContextFilterOperator.NotContains:
                    return value is not null && !array.Contains(value, StringComparer.Ordinal);
                case FlagContextFilterOperator.Set:
                    return array.Length > 0;
                case FlagContextFilterOperator.NotSet:
                    return array.Length == 0;
                default:
                    SetUnsupportedArray(errors, filter.Field, filter.OperatorName);
                    return false;
            }
        }

        var scalar = (string)fieldValue!;
        switch (@operator)
        {
            case FlagContextFilterOperator.Contains:
                return value is not null && ContainsIgnoreCase(scalar, value);
            case FlagContextFilterOperator.NotContains:
                return value is not null && !ContainsIgnoreCase(scalar, value);
            case FlagContextFilterOperator.Gt:
            case FlagContextFilterOperator.Lt:
                {
                    if (!TryParseNumber(scalar, out var left) || !TryParseNumber(value, out var right))
                    {
                        return SetInvalidComparison(errors, filter);
                    }

                    return @operator == FlagContextFilterOperator.Gt ? left > right : left < right;
                }

            case FlagContextFilterOperator.After:
            case FlagContextFilterOperator.Before:
                {
                    if (!TryParseNumber(value, out var days) || !TryParseDate(scalar, out var timestamp))
                    {
                        return SetInvalidComparison(errors, filter);
                    }

                    DateTimeOffset daysAgo;
                    try
                    {
                        daysAgo = now.AddDays(-days);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        return SetInvalidComparison(errors, filter);
                    }

                    return @operator == FlagContextFilterOperator.After ? timestamp > daysAgo : timestamp < daysAgo;
                }

            case FlagContextFilterOperator.DateAfter:
            case FlagContextFilterOperator.DateBefore:
                {
                    if (!TryParseDate(scalar, out var left) || value is null || !TryParseDate(value, out var right))
                    {
                        return SetInvalidComparison(errors, filter);
                    }

                    return @operator == FlagContextFilterOperator.DateAfter ? left >= right : left <= right;
                }

            case FlagContextFilterOperator.Set:
                return scalar.Length != 0;
            case FlagContextFilterOperator.NotSet:
                return scalar.Length == 0;
            case FlagContextFilterOperator.Is:
                return scalar == value;
            case FlagContextFilterOperator.IsNot:
                return scalar != value;
            case FlagContextFilterOperator.IsTrue:
                return scalar == "true";
            case FlagContextFilterOperator.IsFalse:
                return scalar == "false";
            default:
                return SetInvalidComparison(errors, filter);
        }
    }

    private static bool ContainsIgnoreCase(string fieldValue, string comparisonValue)
    {
#if NETSTANDARD2_0
        return fieldValue.IndexOf(comparisonValue, StringComparison.OrdinalIgnoreCase) >= 0;
#else
        return fieldValue.Contains(comparisonValue, StringComparison.OrdinalIgnoreCase);
#endif
    }

    // Approximates JavaScript Number(): surrounding whitespace is ignored and an empty string is zero.
    private static bool TryParseNumber(string? value, out double number)
    {
        number = 0;
        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
               !double.IsNaN(number) &&
               !double.IsInfinity(number);
    }

    private static bool TryParseDate(string value, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date) ||
        DateTimeOffset.TryParse(value, out date);

    private static void PropagateErrors(
        IEnumerable<ReflagEvaluationError> sourceErrors,
        string sourceType,
        string sourceKey,
        ErrorSet diagnostics,
        ErrorSet? errors)
    {
        foreach (var error in sourceErrors)
        {
            var diagnostic = error.Source is not null
                ? error
                : new ReflagEvaluationError
                {
                    Code = error.Code,
                    Field = error.Field,
                    Operator = error.Operator,
                    Message = error.Message,
                    Source = new ReflagEvaluationErrorSource { Type = sourceType, Key = sourceKey },
                };
            var key = string.Join(
                "\u0000",
                "propagated",
                diagnostic.Code,
                diagnostic.Field ?? string.Empty,
                diagnostic.Operator ?? string.Empty,
                diagnostic.Message,
                diagnostic.Source!.Type,
                diagnostic.Source.Key);
            diagnostics.Set(key, diagnostic);
            errors?.Set(key, diagnostic);
        }
    }

    private static void SetMissingField(ErrorSet errors, string field)
    {
        errors.Set($"missing:{field}", new ReflagEvaluationError
        {
            Code = MissingContextField,
            Field = field,
            Message = $"Context field \"{field}\" is required to evaluate targeting rules.",
        });
    }

    private static void SetUnsupportedArray(ErrorSet errors, string field, string @operator)
    {
        errors.Set($"array:{field}:{@operator}", new ReflagEvaluationError
        {
            Code = UnsupportedArrayOperator,
            Field = field,
            Operator = @operator,
            Message = $"Operator {@operator} does not support array-valued context field \"{field}\".",
        });
    }

    private static bool SetInvalidComparison(ErrorSet errors, ContextFilter filter)
    {
        errors.Set($"comparison:{filter.Field}:{filter.OperatorName}", new ReflagEvaluationError
        {
            Code = InvalidComparison,
            Field = filter.Field,
            Operator = filter.OperatorName,
            // Do not log or embed context values; they may contain sensitive data.
            Message = $"Operator {filter.OperatorName} requires valid comparison values for context field \"{filter.Field}\".",
        });
        return false;
    }

    private static void SetInvalidDefinition(ErrorSet errors, string message)
    {
        errors.Set(InvalidKey, new ReflagEvaluationError
        {
            Code = InvalidFlagDefinition,
            Message = message,
        });
    }

    private static PreparedFilter PrepareFilter(FlagFilterDefinition filter)
    {
        return filter switch
        {
            FlagConstantFilterDefinition constant => new ConstantFilter(constant.Value),
            FlagFilterGroupDefinition group => new GroupFilter(
                string.Equals(group.Operator, "and", StringComparison.Ordinal),
                group.Filters.Select(PrepareFilter).ToArray()),
            FlagFilterNegationDefinition negation => new NegationFilter(PrepareFilter(negation.Filter)),
            FlagContextFilterDefinition context => new ContextFilter(
                context.Field,
                context.Operator,
                context.UnknownOperator ?? FlagContextFilterOperatorJsonConverter.GetName(context.Operator),
                context.Values.ToArray(),
                context.Operator is FlagContextFilterOperator.AnyOf or FlagContextFilterOperator.NotAnyOf
                    ? new HashSet<string>(context.Values, StringComparer.Ordinal)
                    : null),
            FlagPercentageRolloutFilterDefinition rollout => new RolloutFilter(
                rollout.Key,
                rollout.PartialRolloutAttribute,
                rollout.PartialRolloutThreshold),
            FlagReferenceFilterDefinition flagReference => new FlagReferenceFilter(flagReference.Key, flagReference.VariantKey),
            SegmentReferenceFilterDefinition segmentReference => new SegmentReferenceFilter(segmentReference.Key),
            _ => new UnsupportedFilter(),
        };
    }

    private static PreparedRule PrepareRule(FlagRuleDefinition rule)
    {
        var filter = PrepareFilter(rule.Filter);
        if (rule.Result is FlagVariantRuleResultDefinition variant)
        {
            return new PreparedRule(rule.Id, filter, new PreparedDestination(variant.VariantKey), null);
        }

        var distribution = (FlagPercentageDistributionRuleResultDefinition)rule.Result;
        var total = 0L;
        var valid = distribution.Allocations.Count > 0;
        var allocations = new PreparedAllocation[distribution.Allocations.Count];
        for (var index = 0; index < allocations.Length; index++)
        {
            var allocation = distribution.Allocations[index];
            var percentage = allocation.Percentage;
            var finite = !double.IsNaN(percentage) && !double.IsInfinity(percentage);
            var units = finite ? (long)Math.Round(percentage * 1000, MidpointRounding.AwayFromZero) : 0;
            valid &= finite && percentage >= 0 && percentage <= 100 && Math.Abs(percentage * 1000 - units) <= 1e-8;
            total += units;
            allocations[index] = new PreparedAllocation(
                total,
                new PreparedDestination(
                    allocation.Destination.Type == "nextRule" ? null : allocation.Destination.VariantKey));
        }

        return new PreparedRule(
            rule.Id,
            filter,
            null,
            new PreparedDistribution(
                distribution.Key,
                distribution.Attribute,
                allocations,
                !valid || total != 100_000 ? $"Invalid percentage allocations in rule {rule.Id}" : null));
    }

    /// <summary>Rejects incomplete or cyclic snapshots, even in short-circuited branches.</summary>
    private void ValidateReferences()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string type, string key)
        {
            var id = $"{type}:{key}";
            var exists = type == "flag" ? _flags.ContainsKey(key) : _segments.ContainsKey(key);
            if (!exists)
            {
                throw new ArgumentException($"Unknown {type} reference \"{key}\"");
            }

            if (visiting.Contains(id))
            {
                throw new ArgumentException($"Circular reference to {type} \"{key}\"");
            }

            if (visited.Contains(id))
            {
                return;
            }

            visiting.Add(id);
            if (type == "flag")
            {
                foreach (var rule in _flags[key].Rules)
                {
                    VisitFilter(rule.Filter);
                }
            }
            else
            {
                VisitFilter(_segments[key]);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        void VisitFilter(PreparedFilter filter)
        {
            switch (filter)
            {
                case FlagReferenceFilter flagReference:
                    Visit("flag", flagReference.Key);
                    if (!_flags[flagReference.Key].Variants.ContainsKey(flagReference.VariantKey))
                    {
                        throw new ArgumentException(
                            $"Unknown variant \"{flagReference.VariantKey}\" in flag reference \"{flagReference.Key}\"");
                    }

                    break;
                case SegmentReferenceFilter segmentReference:
                    Visit("segment", segmentReference.Key);
                    break;
                case GroupFilter group:
                    foreach (var child in group.Filters)
                    {
                        VisitFilter(child);
                    }

                    break;
                case NegationFilter negation:
                    VisitFilter(negation.Filter);
                    break;
            }
        }

        foreach (var key in _flags.Keys)
        {
            Visit("flag", key);
        }

        foreach (var key in _segments.Keys)
        {
            Visit("segment", key);
        }
    }

    private sealed record EvaluationState(IReadOnlyDictionary<string, object?> Context, DateTimeOffset Now);

    private sealed record PreparedFlag(
        string Key,
        string SourceVersionId,
        Dictionary<string, JsonElement> Variants,
        PreparedRule[] Rules);

    private sealed record PreparedRule(
        string Id,
        PreparedFilter Filter,
        PreparedDestination? Variant,
        PreparedDistribution? Distribution);

    private sealed record PreparedDistribution(
        string Key,
        string Attribute,
        PreparedAllocation[] Allocations,
        string? Error);

    private sealed record PreparedAllocation(long UpperBound, PreparedDestination Destination);

    /// <summary>A variant destination, or the next rule when <see cref="VariantKey" /> is null.</summary>
    private sealed record PreparedDestination(string? VariantKey)
    {
        public FlagVariantDestinationDefinition ToDefinition() => VariantKey is null
            ? new FlagVariantDestinationDefinition { Type = "nextRule" }
            : new FlagVariantDestinationDefinition { Type = "variant", VariantKey = VariantKey };
    }

    private abstract record PreparedFilter;

    private sealed record ConstantFilter(bool Value) : PreparedFilter;

    private sealed record ContextFilter(
        string Field,
        FlagContextFilterOperator Operator,
        string OperatorName,
        string[] Values,
        HashSet<string>? ValueSet) : PreparedFilter;

    private sealed record RolloutFilter(string Key, string Attribute, int Threshold) : PreparedFilter;

    private sealed record FlagReferenceFilter(string Key, string VariantKey) : PreparedFilter;

    private sealed record SegmentReferenceFilter(string Key) : PreparedFilter;

    private sealed record GroupFilter(bool IsAnd, PreparedFilter[] Filters) : PreparedFilter;

    private sealed record NegationFilter(PreparedFilter Filter) : PreparedFilter;

    private sealed record UnsupportedFilter : PreparedFilter;

    /// <summary>Insertion-ordered, keyed diagnostics, matching the reference implementation's Map semantics.</summary>
    private sealed class ErrorSet
    {
        private readonly List<string> _keys = new();
        private readonly Dictionary<string, ReflagEvaluationError> _values = new(StringComparer.Ordinal);

        public int Count => _keys.Count;

        public IEnumerable<ReflagEvaluationError> Values => _keys.Select(key => _values[key]);

        public bool Contains(string key) => _values.ContainsKey(key);

        public void Set(string key, ReflagEvaluationError error)
        {
            if (!_values.ContainsKey(key))
            {
                _keys.Add(key);
            }

            _values[key] = error;
        }

        public void SetAll(ErrorSet other)
        {
            foreach (var key in other._keys)
            {
                Set(key, other._values[key]);
            }
        }

        public void Clear()
        {
            _keys.Clear();
            _values.Clear();
        }

        public ReflagEvaluationError[] ToArray() => Values.ToArray();
    }
}
