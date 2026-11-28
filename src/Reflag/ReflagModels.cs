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

/// <summary>Options for <see cref="ReflagClient" />. Unset options fall back to environment variables, then defaults.</summary>
public sealed class ReflagClientOptions
{
    /// <summary>The environment secret key. Defaults to <c>REFLAG_SECRET_KEY</c>. Required unless <see cref="Offline" /> is set.</summary>
    public string? SecretKey { get; init; }

    /// <summary>The Reflag API base URL. Defaults to <c>REFLAG_API_BASE_URL</c>, then <c>https://front.reflag.com</c>.</summary>
    public Uri? ApiBaseUrl { get; init; }

    /// <summary>The logger. Defaults to the host's <c>ILoggerFactory</c> when registered with <c>AddReflag</c>, otherwise no logging.</summary>
    public ILogger? Logger { get; init; }

    /// <summary>The HTTP client used for API requests and push sync.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Durable storage for the last fetched flag definitions, used if Reflag is unavailable at startup. Ignored in offline mode.</summary>
    public IFlagsFallbackProvider? FlagsFallbackProvider { get; init; }

    /// <summary>Timeout for flag definition requests. Defaults to 10 seconds.</summary>
    public TimeSpan? FetchTimeout { get; init; }

    /// <summary>Retries for flag definition requests. Defaults to 3.</summary>
    public int? FlagsFetchRetries { get; init; }

    /// <summary>Tracking event batching options.</summary>
    public ReflagBatchOptions? Batch { get; init; }

    /// <summary>Base Boolean overrides. Specify at most one of the four override options.</summary>
    public IReadOnlyDictionary<string, bool>? FlagOverrides { get; init; }

    /// <summary>Context-dependent Boolean overrides. Specify at most one of the four override options.</summary>
    public Func<ReflagContext, IReadOnlyDictionary<string, bool>>? FlagOverridesFactory { get; init; }

    /// <summary>
    /// Base overrides with arbitrary JSON-serializable values, including Booleans and null.
    /// Values are captured when the client is constructed and take precedence over environment overrides.
    /// Specify at most one of the four override options.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? JsonFlagOverrides { get; init; }

    /// <summary>
    /// JSON-valued overrides computed for each normalized evaluation context, taking precedence over
    /// environment overrides. A null result is treated as an empty dictionary.
    /// Specify at most one of the four override options.
    /// </summary>
    public Func<ReflagContext, IReadOnlyDictionary<string, object?>>? JsonFlagOverridesFactory { get; init; }

    /// <summary>Disables all network I/O. Defaults to <c>REFLAG_OFFLINE</c>. Useful in tests.</summary>
    public bool? Offline { get; init; }

    /// <summary>How flag definitions are kept fresh. Defaults to <see cref="ReflagFlagsSyncMode.Push" />.</summary>
    public ReflagFlagsSyncMode? FlagsSyncMode { get; init; }

    /// <summary>Overrides the push sync endpoint.</summary>
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
