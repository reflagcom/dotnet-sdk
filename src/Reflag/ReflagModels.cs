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
