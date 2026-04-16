using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage.Blobs;
using Google;
using Google.Cloud.Storage.V1;
using Reflag.Internal;
using StackExchange.Redis;

namespace Reflag;

public static class ReflagFallbackProviders
{
    public static IFlagsFallbackProvider Static(IReadOnlyDictionary<string, bool> flags)
    {
        ThrowHelpers.ThrowIfNull(flags, nameof(flags));
        return new StaticFlagsFallbackProvider(CollectionHelpers.ToDictionary(flags, StringComparer.Ordinal));
    }

    public static IFlagsFallbackProvider File(FileFallbackProviderOptions? options = null)
    {
        return new FileFlagsFallbackProvider(options ?? new FileFallbackProviderOptions());
    }

    public static IFlagsFallbackProvider AzureBlob(AzureBlobFallbackProviderOptions options)
    {
        ThrowHelpers.ThrowIfNull(options, nameof(options));
        return new AzureBlobFlagsFallbackProvider(options);
    }

    public static IFlagsFallbackProvider Redis(RedisFallbackProviderOptions? options = null)
    {
        return new RedisFlagsFallbackProvider(options ?? new RedisFallbackProviderOptions());
    }

    public static IFlagsFallbackProvider S3(S3FallbackProviderOptions options)
    {
        ThrowHelpers.ThrowIfNull(options, nameof(options));
        ThrowHelpers.ThrowIfNullOrWhitespace(options.Bucket, nameof(options.Bucket));
        return new S3FlagsFallbackProvider(options);
    }

    public static IFlagsFallbackProvider Gcs(GcsFallbackProviderOptions options)
    {
        ThrowHelpers.ThrowIfNull(options, nameof(options));
        ThrowHelpers.ThrowIfNullOrWhitespace(options.Bucket, nameof(options.Bucket));
        return new GcsFlagsFallbackProvider(options);
    }

    private sealed class StaticFlagsFallbackProvider(IReadOnlyDictionary<string, bool> flags) : IFlagsFallbackProvider
    {
        public Task<FlagsFallbackSnapshot?> LoadAsync(
            FlagsFallbackProviderContext context,
            CancellationToken cancellationToken = default)
        {
            var snapshot = new FlagsFallbackSnapshot
            {
                SchemaVersion = 1,
                SavedAt = DateTimeOffset.UtcNow,
                Flags = flags.Select(pair => new FlagDefinition
                {
                    Key = pair.Key,
                    Targeting = new FlagTargetingDefinition
                    {
                        Version = 0,
                        Rules =
                        [
                            new FlagTargetingRuleDefinition
                            {
                                Filter = new FlagConstantFilterDefinition
                                {
                                    Value = true,
                                },
                                Value = pair.Value,
                            },
                        ],
                    },
                }).ToArray(),
            };

            return Task.FromResult<FlagsFallbackSnapshot?>(snapshot);
        }

        public Task SaveAsync(
            FlagsFallbackProviderContext context,
            FlagsFallbackSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FileFlagsFallbackProvider(FileFallbackProviderOptions options) : IFlagsFallbackProvider
    {
        private readonly string _directory = options.Directory ?? Path.Combine(Environment.CurrentDirectory, ".reflag");

        public async Task<FlagsFallbackSnapshot?> LoadAsync(
            FlagsFallbackProviderContext context,
            CancellationToken cancellationToken = default)
        {
            var path = BuildPath(context.SecretKeyHash);
            try
            {
                using var stream = global::System.IO.File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<FlagsFallbackSnapshot>(stream, ReflagJson.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        public async Task SaveAsync(
            FlagsFallbackProviderContext context,
            FlagsFallbackSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(_directory);
            var path = BuildPath(context.SecretKeyHash);
            var tempPath = $"{path}.tmp-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

            using (var stream = global::System.IO.File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, ReflagJson.Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            ReplaceFile(tempPath, path);
        }

        private string BuildPath(string secretKeyHash)
        {
            return Path.Combine(_directory, BuildSnapshotFileName(secretKeyHash));
        }
    }

    internal sealed class AzureBlobFlagsFallbackProvider : IFlagsFallbackProvider
    {
        private const string DefaultBlobNamePrefix = "reflag/flags-fallback";
        private readonly IAzureBlobContainerClient _containerClient;
        private readonly string _blobNamePrefix;

        public AzureBlobFlagsFallbackProvider(AzureBlobFallbackProviderOptions options)
            : this(options, CreateContainerClient(options))
        {
        }

        internal AzureBlobFlagsFallbackProvider(
            AzureBlobFallbackProviderOptions options,
            IAzureBlobContainerClient containerClient)
        {
            ThrowHelpers.ThrowIfNull(options, nameof(options));
            ThrowHelpers.ThrowIfNull(containerClient, nameof(containerClient));

            _containerClient = containerClient;
            _blobNamePrefix = NormalizeObjectPrefix(options.BlobNamePrefix, DefaultBlobNamePrefix);
        }

        public async Task<FlagsFallbackSnapshot?> LoadAsync(
            FlagsFallbackProviderContext context,
            CancellationToken cancellationToken = default)
        {
            var blobName = BuildBlobName(context.SecretKeyHash);
            var rawSnapshot = await _containerClient.DownloadStringAsync(blobName, cancellationToken).ConfigureAwait(false);
            return rawSnapshot is null
                ? null
                : JsonSerializer.Deserialize<FlagsFallbackSnapshot>(rawSnapshot, ReflagJson.Options);
        }

        public async Task SaveAsync(
            FlagsFallbackProviderContext context,
            FlagsFallbackSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            var blobName = BuildBlobName(context.SecretKeyHash);
            var rawSnapshot = JsonSerializer.Serialize(snapshot, ReflagJson.Options);
            await _containerClient.UploadStringAsync(blobName, rawSnapshot, cancellationToken).ConfigureAwait(false);
        }

        internal string BuildBlobName(string secretKeyHash)
        {
            return BuildObjectName(_blobNamePrefix, secretKeyHash);
        }

        private static IAzureBlobContainerClient CreateContainerClient(AzureBlobFallbackProviderOptions options)
        {
            if (options.ContainerClient is not null)
            {
                return new AzureBlobContainerClientAdapter(options.ContainerClient);
            }

            if (string.IsNullOrWhiteSpace(options.ContainerName))
            {
                throw new ArgumentException("ContainerName must be provided when ContainerClient is not supplied.", nameof(options));
            }

            var connectionString = options.ConnectionString ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Azure Blob fallback provider requires ConnectionString or AZURE_STORAGE_CONNECTION_STRING when ContainerClient is not supplied.");
            }

            return new AzureBlobContainerClientAdapter(new BlobContainerClient(connectionString, options.ContainerName));
        }
    }

    internal sealed class RedisFlagsFallbackProvider : IFlagsFallbackProvider
    {
        private const string DefaultKeyPrefix = "reflag:flags-fallback";
        private readonly IRedisStringStore _store;
        private readonly string _keyPrefix;

        public RedisFlagsFallbackProvider(RedisFallbackProviderOptions options)
            : this(options, CreateStore(options))
        {
        }

        internal RedisFlagsFallbackProvider(
            RedisFallbackProviderOptions options,
            IRedisStringStore store)
        {
            ThrowHelpers.ThrowIfNull(options, nameof(options));
            ThrowHelpers.ThrowIfNull(store, nameof(store));

            _store = store;
            _keyPrefix = NormalizeRedisPrefix(options.KeyPrefix, DefaultKeyPrefix);
        }

        public async Task<FlagsFallbackSnapshot?> LoadAsync(
            FlagsFallbackProviderContext context,
            CancellationToken cancellationToken = default)
        {
            var rawSnapshot = await _store.GetStringAsync(BuildRedisKey(context.SecretKeyHash), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(rawSnapshot)
                ? null
                : JsonSerializer.Deserialize<FlagsFallbackSnapshot>(rawSnapshot!, ReflagJson.Options);
        }

        public Task SaveAsync(
            FlagsFallbackProviderContext context,
            FlagsFallbackSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            return _store.SetStringAsync(
                BuildRedisKey(context.SecretKeyHash),
                JsonSerializer.Serialize(snapshot, ReflagJson.Options),
                cancellationToken);
        }

        internal string BuildRedisKey(string secretKeyHash)
        {
            return $"{_keyPrefix}:{GetSecretKeyHashPrefix(secretKeyHash)}";
        }

        private static IRedisStringStore CreateStore(RedisFallbackProviderOptions options)
        {
            if (options.Database is not null)
            {
                return new RedisDatabaseAdapter(options.Database);
            }

            if (options.ConnectionMultiplexer is not null)
            {
                return new RedisDatabaseAdapter(options.ConnectionMultiplexer.GetDatabase());
            }

            var connectionString = options.ConnectionString ?? Environment.GetEnvironmentVariable("REDIS_URL");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Redis fallback provider requires ConnectionString or REDIS_URL when Database/ConnectionMultiplexer is not supplied.");
            }

            return new LazyRedisStringStore(connectionString);
        }
    }

    internal sealed class S3FlagsFallbackProvider : IFlagsFallbackProvider
    {
        private const string DefaultKeyPrefix = "reflag/flags-fallback";
        private readonly string _bucket;
        private readonly IS3ObjectStore _store;
        private readonly string _keyPrefix;

        public S3FlagsFallbackProvider(S3FallbackProviderOptions options)
            : this(options, CreateStore(options))
        {
        }

        internal S3FlagsFallbackProvider(
            S3FallbackProviderOptions options,
            IS3ObjectStore store)
        {
            ThrowHelpers.ThrowIfNull(options, nameof(options));
            ThrowHelpers.ThrowIfNull(store, nameof(store));
            ThrowHelpers.ThrowIfNullOrWhitespace(options.Bucket, nameof(options.Bucket));

            _bucket = options.Bucket;
            _store = store;
            _keyPrefix = NormalizeObjectPrefix(options.KeyPrefix, DefaultKeyPrefix);
        }

        public async Task<FlagsFallbackSnapshot?> LoadAsync(
            FlagsFallbackProviderContext context,
            CancellationToken cancellationToken = default)
        {
            var rawSnapshot = await _store.GetStringAsync(_bucket, BuildObjectKey(context.SecretKeyHash), cancellationToken).ConfigureAwait(false);
            return rawSnapshot is null
                ? null
                : JsonSerializer.Deserialize<FlagsFallbackSnapshot>(rawSnapshot, ReflagJson.Options);
        }

        public Task SaveAsync(
            FlagsFallbackProviderContext context,
            FlagsFallbackSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            return _store.PutStringAsync(
                _bucket,
                BuildObjectKey(context.SecretKeyHash),
                JsonSerializer.Serialize(snapshot, ReflagJson.Options),
                cancellationToken);
        }

        internal string BuildObjectKey(string secretKeyHash)
        {
            return BuildObjectName(_keyPrefix, secretKeyHash);
        }

        private static IS3ObjectStore CreateStore(S3FallbackProviderOptions options)
        {
            return new S3ObjectStoreAdapter(options.Client ?? new AmazonS3Client());
        }
    }

    internal sealed class GcsFlagsFallbackProvider : IFlagsFallbackProvider
    {
        private const string DefaultKeyPrefix = "reflag/flags-fallback";
        private readonly string _bucket;
        private readonly IGcsObjectStore _store;
        private readonly string _keyPrefix;

        public GcsFlagsFallbackProvider(GcsFallbackProviderOptions options)
            : this(options, CreateStore(options))
        {
        }

        internal GcsFlagsFallbackProvider(
            GcsFallbackProviderOptions options,
            IGcsObjectStore store)
        {
            ThrowHelpers.ThrowIfNull(options, nameof(options));
            ThrowHelpers.ThrowIfNull(store, nameof(store));
            ThrowHelpers.ThrowIfNullOrWhitespace(options.Bucket, nameof(options.Bucket));

            _bucket = options.Bucket;
            _store = store;
            _keyPrefix = NormalizeObjectPrefix(options.KeyPrefix, DefaultKeyPrefix);
        }

        public async Task<FlagsFallbackSnapshot?> LoadAsync(
            FlagsFallbackProviderContext context,
            CancellationToken cancellationToken = default)
        {
            var rawSnapshot = await _store.DownloadStringAsync(_bucket, BuildObjectKey(context.SecretKeyHash), cancellationToken).ConfigureAwait(false);
            return rawSnapshot is null
                ? null
                : JsonSerializer.Deserialize<FlagsFallbackSnapshot>(rawSnapshot, ReflagJson.Options);
        }

        public Task SaveAsync(
            FlagsFallbackProviderContext context,
            FlagsFallbackSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            return _store.UploadStringAsync(
                _bucket,
                BuildObjectKey(context.SecretKeyHash),
                JsonSerializer.Serialize(snapshot, ReflagJson.Options),
                cancellationToken);
        }

        internal string BuildObjectKey(string secretKeyHash)
        {
            return BuildObjectName(_keyPrefix, secretKeyHash);
        }

        private static IGcsObjectStore CreateStore(GcsFallbackProviderOptions options)
        {
            return new GcsObjectStoreAdapter(options.Client ?? StorageClient.Create());
        }
    }

    internal interface IAzureBlobContainerClient
    {
        Task<string?> DownloadStringAsync(string blobName, CancellationToken cancellationToken = default);

        Task UploadStringAsync(string blobName, string content, CancellationToken cancellationToken = default);
    }

    internal interface IRedisStringStore
    {
        Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default);

        Task SetStringAsync(string key, string value, CancellationToken cancellationToken = default);
    }

    internal interface IS3ObjectStore
    {
        Task<string?> GetStringAsync(string bucket, string key, CancellationToken cancellationToken = default);

        Task PutStringAsync(string bucket, string key, string value, CancellationToken cancellationToken = default);
    }

    internal interface IGcsObjectStore
    {
        Task<string?> DownloadStringAsync(string bucket, string objectName, CancellationToken cancellationToken = default);

        Task UploadStringAsync(string bucket, string objectName, string content, CancellationToken cancellationToken = default);
    }

    internal sealed class AzureBlobContainerClientAdapter(BlobContainerClient containerClient) : IAzureBlobContainerClient
    {
        public async Task<string?> DownloadStringAsync(string blobName, CancellationToken cancellationToken = default)
        {
            try
            {
                var blobClient = containerClient.GetBlobClient(blobName);
                var response = await blobClient.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
                return response.Value.Content.ToString();
            }
            catch (RequestFailedException error) when (error.Status == 404)
            {
                return null;
            }
        }

        public async Task UploadStringAsync(string blobName, string content, CancellationToken cancellationToken = default)
        {
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var blobClient = containerClient.GetBlobClient(blobName);
            await blobClient.UploadAsync(
                BinaryData.FromString(content),
                overwrite: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed class RedisDatabaseAdapter(IDatabase database) : IRedisStringStore
    {
        public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
        {
            var result = await database.StringGetAsync(key).ConfigureAwait(false);
            return result.IsNull ? null : result.ToString();
        }

        public Task SetStringAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return database.StringSetAsync(key, value);
        }
    }

    internal sealed class LazyRedisStringStore(string connectionString) : IRedisStringStore
    {
        private readonly object _gate = new();
        private Task<RedisConnectionHandle>? _connectionTask;

        public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
        {
            var handle = await GetHandleAsync().ConfigureAwait(false);
            return await handle.Store.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
        }

        public async Task SetStringAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            var handle = await GetHandleAsync().ConfigureAwait(false);
            await handle.Store.SetStringAsync(key, value, cancellationToken).ConfigureAwait(false);
        }

        private Task<RedisConnectionHandle> GetHandleAsync()
        {
            lock (_gate)
            {
                return _connectionTask ??= ConnectAsync(connectionString);
            }
        }

        private static async Task<RedisConnectionHandle> ConnectAsync(string connectionString)
        {
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
            return new RedisConnectionHandle(multiplexer, new RedisDatabaseAdapter(multiplexer.GetDatabase()));
        }

        private sealed record RedisConnectionHandle(IConnectionMultiplexer Multiplexer, IRedisStringStore Store);
    }

    internal sealed class S3ObjectStoreAdapter(IAmazonS3 client) : IS3ObjectStore
    {
        public async Task<string?> GetStringAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            try
            {
                using var response = await client.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                }, cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: false);
                return await AsyncHelpers.ReadToEndAsync(reader, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception error) when (
                error.StatusCode == HttpStatusCode.NotFound ||
                string.Equals(error.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        public Task PutStringAsync(string bucket, string key, string value, CancellationToken cancellationToken = default)
        {
            return client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = value,
                ContentType = "application/json",
            }, cancellationToken);
        }
    }

    internal sealed class GcsObjectStoreAdapter(StorageClient client) : IGcsObjectStore
    {
        public async Task<string?> DownloadStringAsync(string bucket, string objectName, CancellationToken cancellationToken = default)
        {
            try
            {
                using var stream = new MemoryStream();
                await client.DownloadObjectAsync(bucket, objectName, stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (GoogleApiException error) when (
                error.HttpStatusCode == HttpStatusCode.NotFound ||
                error.Error?.Code == 404)
            {
                return null;
            }
        }

        public async Task UploadStringAsync(string bucket, string objectName, string content, CancellationToken cancellationToken = default)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await client.UploadObjectAsync(bucket, objectName, "application/json", stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void ReplaceFile(string sourcePath, string destinationPath)
    {
        if (global::System.IO.File.Exists(destinationPath))
        {
            global::System.IO.File.Delete(destinationPath);
        }

        global::System.IO.File.Move(sourcePath, destinationPath);
    }

    private static string BuildSnapshotFileName(string secretKeyHash)
    {
        return $"flags-fallback-{GetSecretKeyHashPrefix(secretKeyHash)}.json";
    }

    private static string BuildObjectName(string prefix, string secretKeyHash)
    {
        var fileName = BuildSnapshotFileName(secretKeyHash);
        return string.IsNullOrEmpty(prefix) ? fileName : $"{prefix}/{fileName}";
    }

    private static string NormalizeObjectPrefix(string? prefix, string defaultPrefix)
    {
        var effectivePrefix = string.IsNullOrWhiteSpace(prefix) ? defaultPrefix : prefix!.Trim();
        return effectivePrefix.Trim('/');
    }

    private static string NormalizeRedisPrefix(string? prefix, string defaultPrefix)
    {
        var effectivePrefix = string.IsNullOrWhiteSpace(prefix) ? defaultPrefix : prefix!.Trim();
        return effectivePrefix.TrimEnd(':');
    }

    private static string GetSecretKeyHashPrefix(string secretKeyHash)
    {
        return secretKeyHash.Length >= 16 ? secretKeyHash.Substring(0, 16) : secretKeyHash;
    }
}
