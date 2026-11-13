using System.Text.Json;

namespace Reflag.Internal;

/// <summary>A validated collection and its prepared evaluator, replaced atomically on refresh.</summary>
internal sealed class PreparedFlagCollection
{
    private PreparedFlagCollection(FlagDefinitions definitions, FlagCollectionEvaluator evaluator, bool trackSelections)
    {
        Definitions = definitions;
        Evaluator = evaluator;
        TrackSelections = trackSelections;
    }

    public static PreparedFlagCollection Empty { get; } = new(
        new FlagDefinitions(),
        new FlagCollectionEvaluator(Array.Empty<FlagDefinition>(), Array.Empty<SegmentDefinition>()),
        trackSelections: true);

    public FlagDefinitions Definitions { get; }

    public FlagCollectionEvaluator Evaluator { get; }

    /// <summary>Static SDK values do not identify authored variants, so they are reported as unresolved.</summary>
    public bool TrackSelections { get; }

    /// <summary>Validates and prepares the entire graph before any cached snapshot is replaced.</summary>
    /// <exception cref="ArgumentException">The collection is incomplete, malformed or cyclic.</exception>
    public static PreparedFlagCollection Prepare(
        IReadOnlyList<FlagDefinition>? flags,
        IReadOnlyList<SegmentDefinition>? segments,
        bool trackSelections = true)
    {
        Ensure(flags is not null && segments is not null, "expected a complete flags/segments collection");

        var copiedFlags = new List<FlagDefinition>(flags!.Count);
        foreach (var flag in flags)
        {
            Ensure(
                flag is not null &&
                !string.IsNullOrEmpty(flag.Key) &&
                !string.IsNullOrEmpty(flag.SourceVersionId) &&
                flag.Variants is { Count: > 0 } &&
                flag.Variants.All(pair => pair.Key.Length > 0 && pair.Value.ValueKind != JsonValueKind.Undefined) &&
                flag.Rules is { Count: > 0 },
                "invalid compiled flag");

            var ruleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in flag!.Rules)
            {
                Ensure(
                    rule is not null &&
                    !string.IsNullOrEmpty(rule.Id) &&
                    ruleIds.Add(rule.Id) &&
                    IsValidFilter(rule.Filter) &&
                    IsValidResult(rule.Result, flag.Variants),
                    "invalid compiled rule");
            }

            var last = flag.Rules[flag.Rules.Count - 1];
            Ensure(
                last.Filter is FlagConstantFilterDefinition { Value: true } &&
                last.Result is FlagVariantRuleResultDefinition,
                "compiled flag requires a fixed catch-all");

            copiedFlags.Add(new FlagDefinition
            {
                Key = flag.Key,
                SourceVersionId = flag.SourceVersionId,
                // Detach variant values from any caller-owned JsonDocument.
                Variants = new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(
                    flag.Variants.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)),
                Rules = flag.Rules.ToArray(),
            });
        }

        foreach (var segment in segments!)
        {
            Ensure(segment is not null && segment.Key is not null && IsValidFilter(segment.Filter), "invalid compiled segment");
        }

        var copiedSegments = segments.Select(segment => new SegmentDefinition { Key = segment.Key, Filter = segment.Filter }).ToArray();
        var evaluator = new FlagCollectionEvaluator(copiedFlags, copiedSegments);
        return new PreparedFlagCollection(
            new FlagDefinitions { Flags = copiedFlags.ToArray(), Segments = copiedSegments },
            evaluator,
            trackSelections);
    }

    private static bool IsValidFilter(FlagFilterDefinition? filter)
    {
        return filter switch
        {
            FlagConstantFilterDefinition => true,
            FlagContextFilterDefinition context =>
                context.Field is not null &&
                context.Operator != FlagContextFilterOperator.Unknown &&
                FlagContextFilterOperatorJsonConverter.IsKnown(context.Operator) &&
                context.Values is not null &&
                context.Values.All(value => value is not null),
            FlagPercentageRolloutFilterDefinition rollout =>
                rollout.Key is not null &&
                rollout.PartialRolloutAttribute is not null &&
                rollout.PartialRolloutThreshold is >= 0 and <= 100_000,
            FlagReferenceFilterDefinition flagReference => flagReference.Key is not null && flagReference.VariantKey is not null,
            SegmentReferenceFilterDefinition segmentReference => segmentReference.Key is not null,
            FlagFilterNegationDefinition negation => IsValidFilter(negation.Filter),
            FlagFilterGroupDefinition group =>
                group.Operator is "and" or "or" &&
                group.Filters is not null &&
                group.Filters.All(IsValidFilter),
            _ => false,
        };
    }

    private static bool IsValidResult(FlagRuleResultDefinition? result, IReadOnlyDictionary<string, JsonElement> variants)
    {
        switch (result)
        {
            case FlagVariantRuleResultDefinition variant:
                return variant.VariantKey is not null && variants.ContainsKey(variant.VariantKey);
            case FlagPercentageDistributionRuleResultDefinition distribution:
                if (string.IsNullOrEmpty(distribution.Key) ||
                    string.IsNullOrEmpty(distribution.Attribute) ||
                    distribution.Allocations is not { Count: > 0 })
                {
                    return false;
                }

                var sum = 0d;
                foreach (var allocation in distribution.Allocations)
                {
                    if (allocation is null ||
                        allocation.Percentage is < 0 or > 100 ||
                        Math.Floor(allocation.Percentage) != allocation.Percentage ||
                        allocation.Destination is null)
                    {
                        return false;
                    }

                    var destinationValid = allocation.Destination.Type switch
                    {
                        "nextRule" => true,
                        "variant" => allocation.Destination.VariantKey is not null &&
                                     variants.ContainsKey(allocation.Destination.VariantKey),
                        _ => false,
                    };
                    if (!destinationValid)
                    {
                        return false;
                    }

                    sum += allocation.Percentage;
                }

                return sum == 100;
            default:
                return false;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}
