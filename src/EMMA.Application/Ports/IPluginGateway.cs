using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// How the host talks to a running plugin, whatever transport it uses. The Wasm host
/// implements this; a future out-of-process transport implements the same port, so
/// nothing above it changes. Results are raw: validation happens in the pipelines.
/// Expected failures come back as a failed <see cref="Outcome{T}"/>; only bugs throw.
/// </summary>
public interface IPluginGateway
{
    /// <summary>Gets what the plugin says about itself. Implementations cache the result.</summary>
    /// <param name="plugin">The plugin to ask.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plugin's declaration.</returns>
    Task<Outcome<PluginInfo>> DescribeAsync(PluginId plugin, CancellationToken cancellationToken);

    /// <summary>Searches the plugin's catalog.</summary>
    /// <param name="plugin">The plugin to ask.</param>
    /// <param name="query">The search request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A page of raw nodes.</returns>
    Task<Outcome<PluginNodePage>> SearchAsync(PluginId plugin, Query query, CancellationToken cancellationToken);

    /// <summary>Lists the children of a node, or the browse root when <paramref name="parent"/> is <see langword="null"/>.</summary>
    /// <param name="plugin">The plugin to ask.</param>
    /// <param name="parent">The plugin-local parent id, or <see langword="null"/> for the root.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A page of raw nodes.</returns>
    Task<Outcome<PluginNodePage>> ChildrenAsync(
        PluginId plugin,
        LocalNodeId? parent,
        PageRequest request,
        CancellationToken cancellationToken);

    /// <summary>Fetches nodes by id. Unknown ids are omitted.</summary>
    /// <param name="plugin">The plugin to ask.</param>
    /// <param name="ids">The plugin-local ids.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The raw nodes found.</returns>
    Task<Outcome<IReadOnlyList<PluginNode>>> GetAsync(
        PluginId plugin,
        IReadOnlyList<LocalNodeId> ids,
        CancellationToken cancellationToken);

    /// <summary>Resolves a node to its assets for a role.</summary>
    /// <param name="plugin">The plugin to ask.</param>
    /// <param name="request">What to resolve.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The assets.</returns>
    Task<Outcome<IReadOnlyList<Asset>>> ResolveAsync(
        PluginId plugin,
        ResolveRequest request,
        CancellationToken cancellationToken);

    /// <summary>Reads bytes from a proxied asset.</summary>
    /// <param name="plugin">The plugin that issued the token.</param>
    /// <param name="token">The opaque token.</param>
    /// <param name="range">The range to read, or <see langword="null"/> for everything.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes.</returns>
    Task<Outcome<Chunk>> ReadAsync(
        PluginId plugin,
        string token,
        ByteRange? range,
        CancellationToken cancellationToken);
}
