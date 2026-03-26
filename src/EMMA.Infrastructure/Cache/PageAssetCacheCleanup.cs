namespace EMMA.Infrastructure.Cache;

/// <summary>
/// Cleanup helper for disk-spilled page assets.
/// </summary>
public static class PageAssetCacheCleanup
{
    public static Task<int> CleanupAsync(PageAssetCacheOptions options, CancellationToken cancellationToken)
    {
        if (options.DiskRetentionDays <= 0)
        {
            return Task.FromResult(0);
        }

        if (!Directory.Exists(options.DiskRootDirectory))
        {
            return Task.FromResult(0);
        }

        var cutoff = DateTime.UtcNow.AddDays(-options.DiskRetentionDays);
        var deleted = 0;

        foreach (var path in Directory.EnumerateFiles(options.DiskRootDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    info.Delete();
                    deleted++;
                }
            }
            catch
            {
            }
        }

        return Task.FromResult(deleted);
    }
}
