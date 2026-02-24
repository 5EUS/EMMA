using EMMA.Domain;
using EMMA.Infrastructure.Cache;

namespace EMMA.Tests.Application;

public sealed class PageAssetCacheBudgetTests
{
    [Fact]
    public async Task CacheHit_ReturnsStoredAsset()
    {
        var options = new PageAssetCacheOptions(
            MemoryBudgetBytes: 1024,
            DiskBudgetBytes: 0,
            DiskRootDirectory: Path.Combine(Path.GetTempPath(), "emma-cache-tests", Guid.NewGuid().ToString("N")),
            DiskRetentionDays: 7);

        var cache = new BoundedPageAssetCache(options);
        var payload = new byte[] { 1, 2, 3 };
        var asset = new MediaPageAsset("image/jpeg", payload, DateTimeOffset.UtcNow);

        await cache.SetAsync("page-1", asset, CancellationToken.None);
        var cached = await cache.GetAsync("page-1", CancellationToken.None);

        Assert.NotNull(cached);
        Assert.Equal("image/jpeg", cached!.ContentType);
        Assert.Equal(payload, cached.Payload);
    }

    [Fact]
    public async Task CacheEvicts_LruEntry_WhenOverMemoryBudget()
    {
        var options = new PageAssetCacheOptions(
            MemoryBudgetBytes: 500,
            DiskBudgetBytes: 0,
            DiskRootDirectory: Path.Combine(Path.GetTempPath(), "emma-cache-tests", Guid.NewGuid().ToString("N")),
            DiskRetentionDays: 7);

        var cache = new BoundedPageAssetCache(options);

        var first = new MediaPageAsset("image/jpeg", new byte[400], DateTimeOffset.UtcNow);
        var second = new MediaPageAsset("image/jpeg", new byte[400], DateTimeOffset.UtcNow);

        await cache.SetAsync("page-1", first, CancellationToken.None);
        await cache.SetAsync("page-2", second, CancellationToken.None);

        var evicted = await cache.GetAsync("page-1", CancellationToken.None);
        var retained = await cache.GetAsync("page-2", CancellationToken.None);

        Assert.Null(evicted);
        Assert.NotNull(retained);
    }

    [Fact]
    public async Task CacheSpill_StaysWithinDiskBudget()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-cache-tests", Guid.NewGuid().ToString("N"));
        var options = new PageAssetCacheOptions(
            MemoryBudgetBytes: 0,
            DiskBudgetBytes: 1000,
            DiskRootDirectory: tempRoot,
            DiskRetentionDays: 7);

        var cache = new BoundedPageAssetCache(options);

        for (var i = 0; i < 3; i++)
        {
            var payload = new byte[600];
            payload[0] = (byte)i;
            var asset = new MediaPageAsset("image/jpeg", payload, DateTimeOffset.UtcNow);
            await cache.SetAsync($"page-{i}", asset, CancellationToken.None);
        }

        var totalBytes = GetDirectorySize(tempRoot);
        Assert.True(totalBytes <= options.DiskBudgetBytes, $"Disk usage {totalBytes} exceeds budget {options.DiskBudgetBytes}.");
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => new FileInfo(file).Length)
            .Sum();
    }
}
