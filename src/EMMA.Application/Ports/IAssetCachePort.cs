using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>A bounded cache of fetched asset bytes shared by every plugin.</summary>
public interface IAssetCachePort
{
    /// <summary>Finds a cached asset.</summary>
    /// <param name="key">The asset key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The payload, or <see langword="null"/> when missing or evicted.</returns>
    Task<AssetPayload?> GetAsync(AssetKey key, CancellationToken cancellationToken);

    /// <summary>Stores an asset, evicting others when over budget.</summary>
    /// <param name="key">The asset key.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the asset is stored (or deliberately skipped).</returns>
    Task SetAsync(AssetKey key, AssetPayload payload, CancellationToken cancellationToken);
}
