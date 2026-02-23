using Microsoft.Extensions.Logging;

namespace EMMA.Storage;

/// <summary>
/// Cleans temp asset files based on retention settings.
/// </summary>
public sealed class TempAssetCleanupService(StorageOptions options, ILogger<TempAssetCleanupService> logger)
{
    private readonly StorageOptions _options = options;
    private readonly ILogger<TempAssetCleanupService> _logger = logger;

    public Task<CleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        var root = _options.TempAssetRootDirectory;
        var retentionDays = _options.TempAssetRetentionDays;
        if (retentionDays <= 0)
        {
            return Task.FromResult(new CleanupResult(0, 0));
        }

        if (!Directory.Exists(root))
        {
            return Task.FromResult(new CleanupResult(0, 0));
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var deletedFiles = 0;
        var deletedDirs = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    info.Delete();
                    deletedFiles++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temp asset: {Path}", path);
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, false);
                    deletedDirs++;
                }
            }
            catch
            {
            }
        }

        return Task.FromResult(new CleanupResult(deletedFiles, deletedDirs));
    }
}

public sealed record CleanupResult(int DeletedFiles, int DeletedDirectories);
