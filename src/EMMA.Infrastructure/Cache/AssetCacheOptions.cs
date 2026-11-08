namespace EMMA.Infrastructure.Cache;

/// <summary>Budgets for <see cref="BoundedAssetCache"/>.</summary>
/// <param name="MemoryBudgetBytes">Bytes kept in memory; zero disables the memory tier.</param>
/// <param name="DiskBudgetBytes">Bytes spilled to disk; zero disables the disk tier.</param>
/// <param name="DiskRoot">Directory for spilled assets. The cache owns it and clears it on start.</param>
/// <param name="MaxAge">How long an entry stays usable after it was fetched.</param>
public sealed record AssetCacheOptions(long MemoryBudgetBytes, long DiskBudgetBytes, string DiskRoot, TimeSpan MaxAge)
{
    /// <summary>Gets the defaults: 64 MiB in memory, 512 MiB on disk under the temp directory, 7 days.</summary>
    public static AssetCacheOptions Default { get; } = new(
        64L * 1024 * 1024,
        512L * 1024 * 1024,
        Path.Combine(Path.GetTempPath(), "emma-cache", "assets"),
        TimeSpan.FromDays(7));
}
