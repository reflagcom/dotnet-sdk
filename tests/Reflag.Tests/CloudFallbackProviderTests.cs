using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class CloudFallbackProviderTests
{
    [Fact]
    public async Task Redis_SaveAsync_uses_expected_default_key()
    {
        var store = new FakeRedisStringStore();
        var provider = new ReflagFallbackProviders.RedisFlagsFallbackProvider(
            new RedisFallbackProviderOptions(),
            store);

        await provider.SaveAsync(
            new FlagsFallbackProviderContext
            {
                SecretKeyHash = "0123456789abcdef-fedcba9876543210",
            },
            CreateSnapshot("flag-a"));

        var write = Assert.Single(store.Writes);
        Assert.Equal("reflag:flags-fallback:0123456789abcdef", write.Key);
        Assert.Equal("flag-a", JsonSerializer.Deserialize<FlagsFallbackSnapshot>(write.Value, ReflagJson.Options)!.Flags[0].Key);
    }

    [Fact]
    public async Task Redis_LoadAsync_returns_null_when_value_is_missing()
    {
        var store = new FakeRedisStringStore();
        var provider = new ReflagFallbackProviders.RedisFlagsFallbackProvider(
            new RedisFallbackProviderOptions(),
            store);

        var snapshot = await provider.LoadAsync(new FlagsFallbackProviderContext
        {
            SecretKeyHash = "0123456789abcdef-fedcba9876543210",
        });

        Assert.Null(snapshot);
        Assert.Equal("reflag:flags-fallback:0123456789abcdef", Assert.Single(store.Reads));
    }

    [Fact]
    public void Redis_BuildRedisKey_trims_custom_prefix()
    {
        var provider = new ReflagFallbackProviders.RedisFlagsFallbackProvider(
            new RedisFallbackProviderOptions
            {
                KeyPrefix = "custom:prefix::",
            },
            new FakeRedisStringStore());

        Assert.Equal("custom:prefix:0123456789abcdef", provider.BuildRedisKey("0123456789abcdef-fedcba9876543210"));
    }

    [Fact]
    public async Task S3_SaveAsync_uses_expected_default_object_key()
    {
        var store = new FakeS3ObjectStore();
        var provider = new ReflagFallbackProviders.S3FlagsFallbackProvider(
            new S3FallbackProviderOptions
            {
                Bucket = "reflag-bucket",
            },
            store);

        await provider.SaveAsync(
            new FlagsFallbackProviderContext
            {
                SecretKeyHash = "0123456789abcdef-fedcba9876543210",
            },
            CreateSnapshot("flag-b"));

        var write = Assert.Single(store.Writes);
        Assert.Equal("reflag-bucket", write.Bucket);
        Assert.Equal("reflag/flags-fallback/flags-fallback-0123456789abcdef.json", write.Key);
        Assert.Equal("flag-b", JsonSerializer.Deserialize<FlagsFallbackSnapshot>(write.Value, ReflagJson.Options)!.Flags[0].Key);
    }

    [Fact]
    public async Task S3_LoadAsync_returns_null_when_object_is_missing()
    {
        var store = new FakeS3ObjectStore();
        var provider = new ReflagFallbackProviders.S3FlagsFallbackProvider(
            new S3FallbackProviderOptions
            {
                Bucket = "reflag-bucket",
            },
            store);

        var snapshot = await provider.LoadAsync(new FlagsFallbackProviderContext
        {
            SecretKeyHash = "0123456789abcdef-fedcba9876543210",
        });

        Assert.Null(snapshot);
        var read = Assert.Single(store.Reads);
        Assert.Equal("reflag-bucket", read.Bucket);
        Assert.Equal("reflag/flags-fallback/flags-fallback-0123456789abcdef.json", read.Key);
    }

    [Fact]
    public void S3_requires_bucket()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ReflagFallbackProviders.S3(new S3FallbackProviderOptions()));

        Assert.Contains(nameof(S3FallbackProviderOptions.Bucket), error.Message);
    }

    [Fact]
    public async Task Gcs_SaveAsync_uses_expected_default_object_key()
    {
        var store = new FakeGcsObjectStore();
        var provider = new ReflagFallbackProviders.GcsFlagsFallbackProvider(
            new GcsFallbackProviderOptions
            {
                Bucket = "reflag-gcs-bucket",
            },
            store);

        await provider.SaveAsync(
            new FlagsFallbackProviderContext
            {
                SecretKeyHash = "abcdef0123456789-fedcba9876543210",
            },
            CreateSnapshot("flag-c"));

        var write = Assert.Single(store.Writes);
        Assert.Equal("reflag-gcs-bucket", write.Bucket);
        Assert.Equal("reflag/flags-fallback/flags-fallback-abcdef0123456789.json", write.ObjectName);
        Assert.Equal("flag-c", JsonSerializer.Deserialize<FlagsFallbackSnapshot>(write.Content, ReflagJson.Options)!.Flags[0].Key);
    }

    [Fact]
    public async Task Gcs_LoadAsync_reads_existing_snapshot()
    {
        var store = new FakeGcsObjectStore
        {
            DownloadContent = JsonSerializer.Serialize(CreateSnapshot("flag-d"), ReflagJson.Options),
        };
        var provider = new ReflagFallbackProviders.GcsFlagsFallbackProvider(
            new GcsFallbackProviderOptions
            {
                Bucket = "reflag-gcs-bucket",
            },
            store);

        var snapshot = await provider.LoadAsync(new FlagsFallbackProviderContext
        {
            SecretKeyHash = "abcdef0123456789-fedcba9876543210",
        });

        Assert.NotNull(snapshot);
        Assert.Equal("flag-d", snapshot.Flags[0].Key);
        var read = Assert.Single(store.Reads);
        Assert.Equal("reflag-gcs-bucket", read.Bucket);
        Assert.Equal("reflag/flags-fallback/flags-fallback-abcdef0123456789.json", read.ObjectName);
    }

    [Fact]
    public void Gcs_BuildObjectKey_trims_custom_prefix()
    {
        var provider = new ReflagFallbackProviders.GcsFlagsFallbackProvider(
            new GcsFallbackProviderOptions
            {
                Bucket = "reflag-gcs-bucket",
                KeyPrefix = "/custom/prefix/",
            },
            new FakeGcsObjectStore());

        Assert.Equal(
            "custom/prefix/flags-fallback-abcdef0123456789.json",
            provider.BuildObjectKey("abcdef0123456789-fedcba9876543210"));
    }

    [Fact]
    public void Redis_requires_connection_when_client_is_not_supplied()
    {
        var original = Environment.GetEnvironmentVariable("REDIS_URL");
        Environment.SetEnvironmentVariable("REDIS_URL", null);

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ReflagFallbackProviders.Redis(new RedisFallbackProviderOptions()));

            Assert.Contains("REDIS_URL", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDIS_URL", original);
        }
    }

    private static FlagsFallbackSnapshot CreateSnapshot(string key)
    {
        return new FlagsFallbackSnapshot
        {
            SchemaVersion = 1,
            SavedAt = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero),
            Flags =
            [
                TestDefinitions.CreateFlag(
                    key,
                    1,
                    new FlagConstantFilterDefinition { Value = true }),
            ],
        };
    }

    private sealed class FakeRedisStringStore : ReflagFallbackProviders.IRedisStringStore
    {
        public string? ReadValue { get; init; }

        public List<string> Reads { get; } = new();

        public List<(string Key, string Value)> Writes { get; } = new();

        public Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
        {
            Reads.Add(key);
            return Task.FromResult(ReadValue);
        }

        public Task SetStringAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Writes.Add((key, value));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeS3ObjectStore : ReflagFallbackProviders.IS3ObjectStore
    {
        public string? ReadValue { get; init; }

        public List<(string Bucket, string Key)> Reads { get; } = new();

        public List<(string Bucket, string Key, string Value)> Writes { get; } = new();

        public Task<string?> GetStringAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            Reads.Add((bucket, key));
            return Task.FromResult(ReadValue);
        }

        public Task PutStringAsync(string bucket, string key, string value, CancellationToken cancellationToken = default)
        {
            Writes.Add((bucket, key, value));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGcsObjectStore : ReflagFallbackProviders.IGcsObjectStore
    {
        public string? DownloadContent { get; init; }

        public List<(string Bucket, string ObjectName)> Reads { get; } = new();

        public List<(string Bucket, string ObjectName, string Content)> Writes { get; } = new();

        public Task<string?> DownloadStringAsync(string bucket, string objectName, CancellationToken cancellationToken = default)
        {
            Reads.Add((bucket, objectName));
            return Task.FromResult(DownloadContent);
        }

        public Task UploadStringAsync(string bucket, string objectName, string content, CancellationToken cancellationToken = default)
        {
            Writes.Add((bucket, objectName, content));
            return Task.CompletedTask;
        }
    }
}
