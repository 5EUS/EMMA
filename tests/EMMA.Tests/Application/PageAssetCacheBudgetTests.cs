using EMMA.Domain;
using EMMA.Infrastructure.Cache;

namespace EMMA.Tests.Application;

public sealed class PageAssetCacheBudgetTests
{
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
