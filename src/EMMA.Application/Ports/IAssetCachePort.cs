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

    /// <summary>Says whether an asset is cached, without reading it.</summary>
    /// <param name="key">The asset key.</param>
    /// <returns><see langword="true"/> when <see cref="GetAsync"/> would find it, as far as can be told without reading it.</returns>
    bool Contains(AssetKey key);

    /// <summary>Stores an asset, evicting others when over budget.</summary>
    /// <param name="key">The asset key.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the asset is stored (or deliberately skipped).</returns>
    Task SetAsync(AssetKey key, AssetPayload payload, CancellationToken cancellationToken);

    /// <summary>Drops every cached asset of a node, for example after the node was refreshed.</summary>
    /// <param name="node">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the node's assets are gone.</returns>
    Task RemoveNodeAsync(NodeRef node, CancellationToken cancellationToken);

    /// <summary>Drops everything.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the cache is empty.</returns>
    Task ClearAsync(CancellationToken cancellationToken);
}
