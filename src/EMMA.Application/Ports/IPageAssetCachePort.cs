using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Cache abstraction for page assets (raw bytes) with bounded storage.
/// </summary>
public interface IPageAssetCachePort
{
    /// <summary>
    /// Retrieves a cached page asset or null if missing.
    /// </summary>
    Task<MediaPageAsset?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a page asset in the cache.
    /// </summary>
    Task SetAsync(string key, MediaPageAsset asset, CancellationToken cancellationToken);
}
