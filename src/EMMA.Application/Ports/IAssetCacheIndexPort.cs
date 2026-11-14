namespace EMMA.Application.Ports;

/// <summary>
/// Remembers which files a disk cache holds, so the cache survives a restart. The index only describes files; the cache
/// owns the files themselves and reconciles the two when it starts.
/// </summary>
public interface IAssetCacheIndexPort
{
    /// <summary>Reads every remembered entry.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The entries, in no particular order.</returns>
    Task<IReadOnlyList<AssetCacheRecord>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or replaces an entry.</summary>
    /// <param name="record">The entry.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the entry is stored.</returns>
    Task UpsertAsync(AssetCacheRecord record, CancellationToken cancellationToken);

    /// <summary>Records when entries were last used.</summary>
    /// <param name="lastUsed">The last-use time per cache key; unknown keys are ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the times are stored.</returns>
    Task TouchAsync(IReadOnlyDictionary<string, DateTimeOffset> lastUsed, CancellationToken cancellationToken);

    /// <summary>Forgets entries.</summary>
    /// <param name="keys">The cache keys; unknown keys are ignored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the entries are gone.</returns>
    Task RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);

    /// <summary>Forgets every entry.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the index is empty.</returns>
    Task ClearAsync(CancellationToken cancellationToken);
}
