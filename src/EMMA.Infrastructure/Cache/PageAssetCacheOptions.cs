namespace EMMA.Infrastructure.Cache;

/// <summary>
/// Configuration for bounded page asset caching.
/// </summary>
public sealed record PageAssetCacheOptions(
    long MemoryBudgetBytes,
    long DiskBudgetBytes,
    string DiskRootDirectory)
{
    public static PageAssetCacheOptions Default { get; } = new(
        64L * 1024 * 1024,
        512L * 1024 * 1024,
        Path.Combine(Path.GetTempPath(), "emma-cache", "page-assets"));
}
