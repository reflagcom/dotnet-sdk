using System.Text.Json.Serialization;
using Amazon.S3;
using Azure.Storage.Blobs;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using Reflag.Internal;
using StackExchange.Redis;

namespace Reflag;

public enum ReflagFlagsSyncMode
{
    Polling,
    Push,
}

public sealed class ReflagBatchOptions
{
    public int? MaxSize { get; init; }

    public TimeSpan? Interval { get; init; }

    /// <summary>
    /// Controls whether the client should try to flush buffered events during application shutdown and disposal.
    /// Defaults to <see langword="true" />.
    /// </summary>
    public bool? FlushOnExit { get; init; }
}

public sealed class ReflagClientOptions
{
    public string? SecretKey { get; init; }

    public Uri? ApiBaseUrl { get; init; }

    public ILogger? Logger { get; init; }

    public HttpClient? HttpClient { get; init; }

    public IReflagHttpTransport? HttpTransport { get; init; }

    public IFlagsFallbackProvider? FlagsFallbackProvider { get; init; }

    public TimeSpan? FetchTimeout { get; init; }

    public int? FlagsFetchRetries { get; init; }

    public ReflagBatchOptions? Batch { get; init; }

    public IReadOnlyDictionary<string, bool>? FlagOverrides { get; init; }

    public Func<ReflagContext, IReadOnlyDictionary<string, bool>>? FlagOverridesFactory { get; init; }

    public bool? Offline { get; init; }

    public ReflagFlagsSyncMode? FlagsSyncMode { get; init; }

    public Uri? FlagsPushUrl { get; init; }
}

public sealed class ReflagUserContext
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Email { get; init; }

    public string? Avatar { get; init; }

    public IReadOnlyDictionary<string, object?> Attributes { get; init; } =
        new Dictionary<string, object?>();
}

public sealed class ReflagCompanyContext
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Avatar { get; init; }

    public IReadOnlyDictionary<string, object?> Attributes { get; init; } =
        new Dictionary<string, object?>();
}

public sealed class ReflagContext
{
    public ReflagUserContext? User { get; init; }

    public ReflagCompanyContext? Company { get; init; }

    public IReadOnlyDictionary<string, object?>? Other { get; init; }

    /// <summary>
    /// Creates a typed <see cref="ReflagContext" /> from an anonymous-object or dictionary-shaped context.
    /// Well-known fields such as <c>Id</c>, <c>Name</c>, <c>Email</c>, and <c>Avatar</c> are mapped directly;
    /// additional fields are normalized into attributes.
    /// </summary>
    public static ReflagContext From(object context)
    {
        return ReflagContextNormalizer.NormalizeLooseContext(context);
    }
}

public sealed class ReflagTelemetryOptions
{
    public bool EnableTelemetry { get; init; } = true;

    public bool? Active { get; init; }
}

public class ReflagTrackOptions
{
    public IReadOnlyDictionary<string, object?>? Attributes { get; init; }

    public bool? Active { get; init; }
}

public sealed class ReflagCompanyTrackOptions : ReflagTrackOptions
{
    public string? UserId { get; init; }
}

public sealed class ReflagEventTrackOptions : ReflagTrackOptions
{
    public string? CompanyId { get; init; }
}

public sealed class RawReflagFlag
{
    public string Key { get; init; } = string.Empty;

    public bool Value { get; init; }

    public int? TargetingVersion { get; init; }

    public IReadOnlyList<bool>? RuleEvaluationResults { get; init; }

    public IReadOnlyList<string>? MissingContextFields { get; init; }
}

public sealed class ReflagBootstrappedFlags
{
    public ReflagContext Context { get; init; } = new();

    public IReadOnlyDictionary<string, RawReflagFlag> Flags { get; init; } =
        new Dictionary<string, RawReflagFlag>();
}

/// <summary>
/// A flag definition as stored in fallback snapshots and returned by <c>GetFlagDefinitions()</c>.
/// </summary>
[JsonConverter(typeof(FlagDefinitionJsonConverter))]
public sealed class FlagDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// The targeting/value-resolution definition for this flag.
    /// This maps to the backend <c>targeting</c> object and is separate from the global <c>flagStateVersion</c>.
    /// </summary>
    [JsonPropertyName("targeting")]
    public FlagTargetingDefinition Targeting { get; init; } = new();
}

/// <summary>
/// The targeting/value-resolution definition for a boolean flag.
/// </summary>
public sealed class FlagTargetingDefinition
{
    /// <summary>
    /// The version of this flag's targeting definition.
    /// This is not the same as the global <c>flagStateVersion</c> and not the same as fallback snapshot schema versioning.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("rules")]
    public IReadOnlyList<FlagTargetingRuleDefinition> Rules { get; init; } = Array.Empty<FlagTargetingRuleDefinition>();
}

[JsonConverter(typeof(FlagTargetingRuleDefinitionJsonConverter))]
public sealed class FlagTargetingRuleDefinition
{
    [JsonPropertyName("filter")]
    public FlagFilterDefinition Filter { get; init; } = default!;

    [JsonPropertyName("value")]
    public bool Value { get; init; }
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
}

public sealed class FlagContextFilterDefinition : FlagFilterDefinition
{
    public override string Type => "context";

    [JsonPropertyName("field")]
    public string Field { get; init; } = string.Empty;

    [JsonPropertyName("operator")]
    public FlagContextFilterOperator Operator { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
}

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

public interface IReflagHttpTransport
{
    Task<ReflagHttpResponse<TResponse>> GetAsync<TResponse>(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<ReflagHttpResponse<TResponse>> PostAsync<TRequest, TResponse>(
        Uri url,
        IReadOnlyDictionary<string, string> headers,
        TRequest body,
        CancellationToken cancellationToken = default);
}

public sealed class ReflagHttpResponse<T>
{
    public int StatusCode { get; init; }

    public bool IsSuccessStatusCode { get; init; }

    public T? Body { get; init; }
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
    /// <summary>
    /// The schema version of the fallback snapshot envelope.
    /// This is not the live <c>flagStateVersion</c> and not a per-flag targeting version.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    public DateTimeOffset SavedAt { get; init; }

    public IReadOnlyList<FlagDefinition> Flags { get; init; } = Array.Empty<FlagDefinition>();
}

public interface IFlagsFallbackProvider
{
    Task<FlagsFallbackSnapshot?> LoadAsync(
        FlagsFallbackProviderContext context,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        FlagsFallbackProviderContext context,
        FlagsFallbackSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

public sealed class FileFallbackProviderOptions
{
    public string? Directory { get; init; }
}

public sealed class AzureBlobFallbackProviderOptions
{
    public BlobContainerClient? ContainerClient { get; init; }

    public string? ConnectionString { get; init; }

    public string? ContainerName { get; init; }

    public string? BlobNamePrefix { get; init; }
}

public sealed class RedisFallbackProviderOptions
{
    public IDatabase? Database { get; init; }

    public IConnectionMultiplexer? ConnectionMultiplexer { get; init; }

    public string? ConnectionString { get; init; }

    public string? KeyPrefix { get; init; }
}

public sealed class S3FallbackProviderOptions
{
    public string Bucket { get; init; } = string.Empty;

    public IAmazonS3? Client { get; init; }

    public string? KeyPrefix { get; init; }
}

public sealed class GcsFallbackProviderOptions
{
    public string Bucket { get; init; } = string.Empty;

    public StorageClient? Client { get; init; }

    public string? KeyPrefix { get; init; }
}
