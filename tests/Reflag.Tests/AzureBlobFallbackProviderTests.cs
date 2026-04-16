using System.Text.Json;
using Reflag.Internal;
using Xunit;

namespace Reflag.Tests;

public sealed class AzureBlobFallbackProviderTests
{
    [Fact]
    public async Task SaveAsync_writes_snapshot_to_expected_default_blob_name()
    {
        var container = new FakeAzureBlobContainerClient();
        var provider = new ReflagFallbackProviders.AzureBlobFlagsFallbackProvider(
            new AzureBlobFallbackProviderOptions(),
            container);

        var snapshot = new FlagsFallbackSnapshot
        {
            SchemaVersion = 1,
            SavedAt = new DateTimeOffset(2026, 4, 16, 12, 0, 0, TimeSpan.Zero),
            Flags =
            [
                TestDefinitions.CreateFlag(
                    "new-dashboard",
                    7,
                    new FlagConstantFilterDefinition { Value = true }),
            ],
        };

        await provider.SaveAsync(
            new FlagsFallbackProviderContext
            {
                SecretKeyHash = "0123456789abcdef-fedcba9876543210",
            },
            snapshot);

        var upload = Assert.Single(container.Uploads);
        Assert.Equal("reflag/flags-fallback/flags-fallback-0123456789abcdef.json", upload.BlobName);

        var savedSnapshot = JsonSerializer.Deserialize<FlagsFallbackSnapshot>(upload.Content, ReflagJson.Options);
        Assert.NotNull(savedSnapshot);
        Assert.Equal(snapshot.SchemaVersion, savedSnapshot.SchemaVersion);
        Assert.Equal(snapshot.SavedAt, savedSnapshot.SavedAt);
        Assert.Single(savedSnapshot.Flags);
        Assert.Equal("new-dashboard", savedSnapshot.Flags[0].Key);
    }

    [Fact]
    public async Task LoadAsync_returns_null_when_blob_is_missing()
    {
        var container = new FakeAzureBlobContainerClient();
        var provider = new ReflagFallbackProviders.AzureBlobFlagsFallbackProvider(
            new AzureBlobFallbackProviderOptions(),
            container);

        var snapshot = await provider.LoadAsync(new FlagsFallbackProviderContext
        {
            SecretKeyHash = "0123456789abcdef-fedcba9876543210",
        });

        Assert.Null(snapshot);
        Assert.Equal(
            "reflag/flags-fallback/flags-fallback-0123456789abcdef.json",
            Assert.Single(container.Downloads));
    }

    [Fact]
    public async Task LoadAsync_reads_snapshot_from_expected_blob_name()
    {
        var container = new FakeAzureBlobContainerClient
        {
            DownloadContent = JsonSerializer.Serialize(
                new FlagsFallbackSnapshot
                {
                    SchemaVersion = 1,
                    SavedAt = new DateTimeOffset(2026, 4, 16, 13, 0, 0, TimeSpan.Zero),
                    Flags =
                    [
                        TestDefinitions.CreateFlag(
                            "beta-flag",
                            3,
                            new FlagConstantFilterDefinition { Value = true }),
                    ],
                },
                ReflagJson.Options),
        };

        var provider = new ReflagFallbackProviders.AzureBlobFlagsFallbackProvider(
            new AzureBlobFallbackProviderOptions(),
            container);

        var snapshot = await provider.LoadAsync(new FlagsFallbackProviderContext
        {
            SecretKeyHash = "abcdef0123456789",
        });

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Single(snapshot.Flags);
        Assert.Equal("beta-flag", snapshot.Flags[0].Key);
        Assert.Equal(
            "reflag/flags-fallback/flags-fallback-abcdef0123456789.json",
            Assert.Single(container.Downloads));
    }

    [Fact]
    public void BuildBlobName_uses_trimmed_custom_prefix()
    {
        var provider = new ReflagFallbackProviders.AzureBlobFlagsFallbackProvider(
            new AzureBlobFallbackProviderOptions
            {
                BlobNamePrefix = "/custom/prefix/",
            },
            new FakeAzureBlobContainerClient());

        Assert.Equal(
            "custom/prefix/flags-fallback-0123456789abcdef.json",
            provider.BuildBlobName("0123456789abcdef-fedcba9876543210"));
    }

    [Fact]
    public void AzureBlob_requires_container_name_when_container_client_is_not_supplied()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ReflagFallbackProviders.AzureBlob(new AzureBlobFallbackProviderOptions
            {
                ConnectionString = "UseDevelopmentStorage=true",
            }));

        Assert.Contains("ContainerName must be provided", error.Message);
    }

    [Fact]
    public void AzureBlob_requires_connection_string_when_container_client_is_not_supplied()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ReflagFallbackProviders.AzureBlob(new AzureBlobFallbackProviderOptions
            {
                ContainerName = "reflag-snapshots",
            }));

        Assert.Contains("AZURE_STORAGE_CONNECTION_STRING", error.Message);
    }

    private sealed class FakeAzureBlobContainerClient : ReflagFallbackProviders.IAzureBlobContainerClient
    {
        public string? DownloadContent { get; init; }

        public List<string> Downloads { get; } = new();

        public List<(string BlobName, string Content)> Uploads { get; } = new();

        public Task<string?> DownloadStringAsync(string blobName, CancellationToken cancellationToken = default)
        {
            Downloads.Add(blobName);
            return Task.FromResult<string?>(DownloadContent);
        }

        public Task UploadStringAsync(string blobName, string content, CancellationToken cancellationToken = default)
        {
            Uploads.Add((blobName, content));
            return Task.CompletedTask;
        }
    }
}
