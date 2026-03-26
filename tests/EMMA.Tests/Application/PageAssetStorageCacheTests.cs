using EMMA.Domain;
using EMMA.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq;

namespace EMMA.Tests.Application;

public sealed class PageAssetStorageCacheTests
{
    [Fact]
    public async Task CacheStoresAndLoadsAssets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var options = new StorageOptions(Path.Combine(tempRoot, "emma.db"))
        {
            TempAssetRootDirectory = tempRoot,
            TempAssetRetentionDays = 7
        };

        var cache = new PageAssetStorageCache(options);
        var payload = new byte[] { 1, 2, 3, 4 };
        var fetchedAt = DateTimeOffset.UtcNow;
        var asset = new MediaPageAsset("image/png", payload, fetchedAt);

        await cache.SetAsync("demo-key", asset, CancellationToken.None);
        var loaded = await cache.GetAsync("demo-key", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("image/png", loaded!.ContentType);
        Assert.Equal(payload, loaded.Payload);
        Assert.Equal(fetchedAt, loaded.FetchedAtUtc);
    }

    [Fact]
    public async Task CleanupRemovesExpiredAssets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var options = new StorageOptions(Path.Combine(tempRoot, "emma.db"))
        {
            TempAssetRootDirectory = tempRoot,
            TempAssetRetentionDays = 1
        };

        var cache = new PageAssetStorageCache(options);
        var asset = new MediaPageAsset("image/jpeg", new byte[] { 9, 9 }, DateTimeOffset.UtcNow);
        await cache.SetAsync("cleanup-key", asset, CancellationToken.None);

        var oldTime = DateTime.UtcNow.AddDays(-2);
        foreach (var path in Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(path, oldTime);
        }

        var cleanup = new TempAssetCleanupService(options, NullLogger<TempAssetCleanupService>.Instance);
        await cleanup.CleanupAsync(CancellationToken.None);

        var remaining = Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories).ToList();
        Assert.Empty(remaining);
    }
}
