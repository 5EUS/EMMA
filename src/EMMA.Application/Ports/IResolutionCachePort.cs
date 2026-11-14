using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// Remembers what a plugin said a node resolves to, so reading the same asset again (every page of a chapter, every
/// range of a video) does not call the plugin each time. A resolution can carry request headers such as cookies and
/// is only valid for a while, so implementations keep it in memory and never persist it.
/// </summary>
public interface IResolutionCachePort
{
    /// <summary>Finds a stored resolution.</summary>
    /// <param name="key">The node, role and hints.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The assets, or <see langword="null"/> when missing or expired.</returns>
    Task<IReadOnlyList<Asset>?> GetAsync(ResolutionKey key, CancellationToken cancellationToken);

    /// <summary>Stores a resolution until it expires, evicting others when over budget.</summary>
    /// <param name="key">The node, role and hints.</param>
    /// <param name="assets">The validated assets.</param>
    /// <param name="expiresAt">When the entry stops being served.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the resolution is stored.</returns>
    Task SetAsync(ResolutionKey key, IReadOnlyList<Asset> assets, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Drops every resolution of a node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when they are gone.</returns>
    Task RemoveNodeAsync(NodeRef node, CancellationToken cancellationToken);

    /// <summary>Drops every resolution a plugin gave, for example because the plugin was reloaded and its tokens died.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when they are gone.</returns>
    Task RemovePluginAsync(PluginId plugin, CancellationToken cancellationToken);

    /// <summary>Drops everything.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the cache is empty.</returns>
    Task ClearAsync(CancellationToken cancellationToken);
}
