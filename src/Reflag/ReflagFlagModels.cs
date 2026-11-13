using System.Text.Json;
using System.Text.Json.Serialization;
using Reflag.Internal;

namespace Reflag;

/// <summary>Identifies the flag or segment where a propagated diagnostic originated.</summary>
public sealed class ReflagEvaluationErrorSource
{
    /// <summary><c>flag</c> or <c>segment</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;
}

/// <summary>A non-fatal flag evaluation diagnostic.</summary>
public sealed class ReflagEvaluationError
{
    /// <summary>
    /// <c>MISSING_CONTEXT_FIELD</c>, <c>UNSUPPORTED_ARRAY_OPERATOR</c>, <c>INVALID_COMPARISON</c>,
    /// <c>INVALID_FLAG_DEFINITION</c> or <c>CLIENT_NOT_INITIALIZED</c>.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("field")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; init; }

    [JsonPropertyName("operator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operator { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>Set when the diagnostic was propagated through a flag or segment reference.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReflagEvaluationErrorSource? Source { get; init; }
}

/// <summary>The result of evaluating one rule of a flag.</summary>
public sealed class ReflagRuleEvaluationResult
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; init; } = string.Empty;

    [JsonPropertyName("matched")]
    public bool Matched { get; init; }

    [JsonPropertyName("allocationIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AllocationIndex { get; init; }

    [JsonPropertyName("destination")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FlagVariantDestinationDefinition? Destination { get; init; }
}

/// <summary>
/// A flag evaluation result with its selection metadata, as included in bootstrap payloads.
/// </summary>
public sealed class RawReflagFlag
{
    [JsonPropertyName("flagKey")]
    public string FlagKey { get; init; } = string.Empty;

    /// <summary>
    /// Whether an authored variant was selected. Overrides, static fallbacks and missing flags are unresolved.
    /// </summary>
    [JsonPropertyName("resolved")]
    public bool Resolved { get; init; }

    /// <summary>
    /// The selected JSON value. <see langword="null" /> means no value; a JSON <c>null</c> value is a
    /// <see cref="JsonElement" /> of kind <see cref="JsonValueKind.Null" />.
    /// </summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Value { get; init; }

    [JsonPropertyName("variantKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VariantKey { get; init; }

    [JsonPropertyName("sourceVersionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceVersionId { get; init; }

    [JsonPropertyName("matchedRuleId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchedRuleId { get; init; }

    [JsonPropertyName("allocationIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AllocationIndex { get; init; }

    [JsonPropertyName("ruleResults")]
    public IReadOnlyList<ReflagRuleEvaluationResult> RuleResults { get; init; } = Array.Empty<ReflagRuleEvaluationResult>();

    [JsonPropertyName("errors")]
    public IReadOnlyList<ReflagEvaluationError> Errors { get; init; } = Array.Empty<ReflagEvaluationError>();
}

/// <summary>
/// Protocol-v2 bootstrap payload for client SDKs. Serializes as
/// <c>{ protocolVersion: 2, context, flags, flagStateVersion? }</c> with the canonical evaluation context.
/// </summary>
[JsonConverter(typeof(ReflagBootstrappedFlagsJsonConverter))]
public sealed class ReflagBootstrappedFlags
{
    public int ProtocolVersion { get; } = 2;

    public ReflagContext Context { get; init; } = new();

    public IReadOnlyDictionary<string, RawReflagFlag> Flags { get; init; } =
        new Dictionary<string, RawReflagFlag>();

    public int? FlagStateVersion { get; init; }
}

/// <summary>
/// A complete collection of compiled flags and the segments they reference, as returned by
/// <c>GET /flags</c>, stored in fallback snapshots and returned by <c>GetFlagDefinitions()</c>.
/// </summary>
public sealed class FlagDefinitions
{
    [JsonPropertyName("flags")]
    public IReadOnlyList<FlagDefinition> Flags { get; init; } = Array.Empty<FlagDefinition>();

    [JsonPropertyName("segments")]
    public IReadOnlyList<SegmentDefinition> Segments { get; init; } = Array.Empty<SegmentDefinition>();
}

/// <summary>A compiled Boolean or multivariate flag.</summary>
public sealed class FlagDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>Identifies the published flag version that produced this definition.</summary>
    [JsonPropertyName("sourceVersionId")]
    public string SourceVersionId { get; init; } = string.Empty;

    /// <summary>JSON variant values keyed by variant key.</summary>
    [JsonPropertyName("variants")]
    public IReadOnlyDictionary<string, JsonElement> Variants { get; init; } = new Dictionary<string, JsonElement>();

    /// <summary>Ordered rules. The last rule is always a fixed catch-all.</summary>
    [JsonPropertyName("rules")]
    public IReadOnlyList<FlagRuleDefinition> Rules { get; init; } = Array.Empty<FlagRuleDefinition>();
}

public sealed class FlagRuleDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("filter")]
    public FlagFilterDefinition Filter { get; init; } = default!;

    [JsonPropertyName("result")]
    public FlagRuleResultDefinition Result { get; init; } = default!;
}

/// <summary>A named predicate that flag rules can reference.</summary>
public sealed class SegmentDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("filter")]
    public FlagFilterDefinition Filter { get; init; } = default!;
}

[JsonConverter(typeof(FlagRuleResultDefinitionJsonConverter))]
public abstract class FlagRuleResultDefinition
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

/// <summary>Selects a fixed variant.</summary>
public sealed class FlagVariantRuleResultDefinition : FlagRuleResultDefinition
{
    public override string Type => "variant";

    [JsonPropertyName("variantKey")]
    public string VariantKey { get; init; } = string.Empty;
}

/// <summary>Buckets the context attribute into weighted allocations.</summary>
public sealed class FlagPercentageDistributionRuleResultDefinition : FlagRuleResultDefinition
{
    public override string Type => "percentageDistribution";

    /// <summary>The hash key (salt) for bucketing.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The flattened context field to bucket on, for example <c>company.id</c>.</summary>
    [JsonPropertyName("attribute")]
    public string Attribute { get; init; } = string.Empty;

    [JsonPropertyName("allocations")]
    public IReadOnlyList<FlagPercentageAllocationDefinition> Allocations { get; init; } =
        Array.Empty<FlagPercentageAllocationDefinition>();
}

public sealed class FlagPercentageAllocationDefinition
{
    [JsonPropertyName("percentage")]
    public double Percentage { get; init; }

    [JsonPropertyName("destination")]
    public FlagVariantDestinationDefinition Destination { get; init; } = default!;
}

/// <summary>Where an allocation or rule leads: a variant, or the next rule.</summary>
public sealed class FlagVariantDestinationDefinition
{
    /// <summary><c>variant</c> or <c>nextRule</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "variant";

    [JsonPropertyName("variantKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VariantKey { get; init; }
}

[JsonConverter(typeof(FlagFilterDefinitionJsonConverter))]
public abstract class FlagFilterDefinition
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class FlagFilterGroupDefinition : FlagFilterDefinition
{
    public override string Type => "group";

    [JsonPropertyName("operator")]
    public string Operator { get; init; } = "and";

    [JsonPropertyName("filters")]
    public IReadOnlyList<FlagFilterDefinition> Filters { get; init; } = Array.Empty<FlagFilterDefinition>();
}

public sealed class FlagFilterNegationDefinition : FlagFilterDefinition
{
    public override string Type => "negation";

    [JsonPropertyName("filter")]
    public FlagFilterDefinition Filter { get; init; } = default!;
}

[JsonConverter(typeof(FlagContextFilterOperatorJsonConverter))]
public enum FlagContextFilterOperator
{
    Is,
    IsNot,
    AnyOf,
    NotAnyOf,
    Contains,
    NotContains,
    Gt,
    Lt,
    After,
    Before,
    DateAfter,
    DateBefore,
    Set,
    NotSet,
    IsTrue,
    IsFalse,

    /// <summary>An unrecognized operator. Collections containing one are rejected.</summary>
    Unknown = -1,
}

[JsonConverter(typeof(FlagContextFilterDefinitionJsonConverter))]
public sealed class FlagContextFilterDefinition : FlagFilterDefinition
{
    public override string Type => "context";

    [JsonPropertyName("field")]
    public string Field { get; init; } = string.Empty;

    [JsonPropertyName("operator")]
    public FlagContextFilterOperator Operator { get; init; }

    // Retain unrecognized wire names for logging and snapshot round trips.
    internal string? UnknownOperator { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
}

/// <summary>Boolean cohort rollout, retained with its original hash keys.</summary>
public sealed class FlagPercentageRolloutFilterDefinition : FlagFilterDefinition
{
    public override string Type => "rolloutPercentage";

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("partialRolloutAttribute")]
    public string PartialRolloutAttribute { get; init; } = string.Empty;

    [JsonPropertyName("partialRolloutThreshold")]
    public int PartialRolloutThreshold { get; init; }
}

public sealed class FlagConstantFilterDefinition : FlagFilterDefinition
{
    public override string Type => "constant";

    [JsonPropertyName("value")]
    public bool Value { get; init; }
}

/// <summary>Matches when another flag selects the given variant key (not the truthiness of its value).</summary>
public sealed class FlagReferenceFilterDefinition : FlagFilterDefinition
{
    public override string Type => "flag";

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("variantKey")]
    public string VariantKey { get; init; } = string.Empty;
}

/// <summary>Matches when the referenced segment's predicate matches.</summary>
public sealed class SegmentReferenceFilterDefinition : FlagFilterDefinition
{
    public override string Type => "segment";

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;
}

public sealed class FlagsFallbackProviderContext
{
    public string SecretKeyHash { get; init; } = string.Empty;
}

/// <summary>
/// A persisted fallback snapshot used when live flag fetches are unavailable.
/// </summary>
public sealed class FlagsFallbackSnapshot
{
    /// <summary>The current snapshot schema version. Snapshots with other versions are ignored.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// The schema version of the fallback snapshot envelope.
    /// This is not the live <c>flagStateVersion</c>.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; init; }

    [JsonPropertyName("flagStateVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FlagStateVersion { get; init; }

    /// <summary><c>static</c> for value-only snapshots that do not identify authored variants.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonPropertyName("flags")]
    public IReadOnlyList<FlagDefinition> Flags { get; init; } = Array.Empty<FlagDefinition>();

    [JsonPropertyName("segments")]
    public IReadOnlyList<SegmentDefinition> Segments { get; init; } = Array.Empty<SegmentDefinition>();
}
