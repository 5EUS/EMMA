using EMMA.Domain;

namespace EMMA.PluginHost.Instances;

/// <summary>
/// One running plugin, whatever carries it: a Wasm component in the native host today, possibly a separate
/// process later. This is the only thing the rest of the host knows about a running plugin, so it must not
/// mention Wasm, the native host, process ids or pipes.
/// </summary>
/// <remarks>
/// Rules every implementation follows (the conformance suite in <c>tests/EMMA.PluginConformance</c> checks them):
/// <list type="bullet">
/// <item>An expected failure (the plugin said no, the upstream is down, a limit was hit) is a failed
/// <see cref="Outcome{T}"/> inside the returned <see cref="PluginCall{T}"/>. Only a bug in the host throws.</item>
/// <item><see cref="PluginCall{T}.Trapped"/> is true when the plugin itself crashed or was stopped, and the
/// instance stays usable for the next call.</item>
/// <item>Calls are independent and may run concurrently.</item>
/// <item>Results are raw: nothing here validates them against the contract limits; the pipelines do.</item>
/// <item>A cancelled token before the call starts throws <see cref="OperationCanceledException"/>; once a call has
/// started, the implementation's own limits bound it.</item>
/// </list>
/// </remarks>
public interface IPluginInstance : IAsyncDisposable
{
    /// <summary>Gets the interface ids of the optional extensions this instance offers, such as <see cref="PluginExtensions.SearchAssist"/>.</summary>
    IReadOnlySet<string> Extensions { get; }

    /// <summary>Asks the plugin to describe itself: the handshake that tells the host what it serves.</summary>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The declaration.</returns>
    Task<PluginCall<PluginInfo>> DescribeAsync(CancellationToken cancellationToken);

    /// <summary>Probes the plugin's liveness and upstream reachability.</summary>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The health report.</returns>
    Task<PluginCall<PluginHealth>> HealthAsync(CancellationToken cancellationToken);

    /// <summary>Searches the plugin's catalog.</summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>A page of raw nodes.</returns>
    Task<PluginCall<PluginNodePage>> SearchAsync(Query query, CancellationToken cancellationToken);

    /// <summary>Lists the children of a node, or the browse root.</summary>
    /// <param name="parent">The plugin-local parent id, or <see langword="null"/> for the root.</param>
    /// <param name="request">Cursor and limit.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>A page of raw nodes; <see cref="ErrorCode.NotFound"/> for an unknown parent.</returns>
    Task<PluginCall<PluginNodePage>> ChildrenAsync(LocalNodeId? parent, PageRequest request, CancellationToken cancellationToken);

    /// <summary>Fetches nodes by id, in request order; unknown ids are omitted.</summary>
    /// <param name="ids">The plugin-local ids.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The raw nodes found.</returns>
    Task<PluginCall<IReadOnlyList<PluginNode>>> GetAsync(IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken);

    /// <summary>Resolves a node to its assets for a role.</summary>
    /// <param name="request">What to resolve.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The assets; <see cref="ErrorCode.Unsupported"/> when the node has none for the role.</returns>
    Task<PluginCall<IReadOnlyList<Asset>>> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken);

    /// <summary>Reads bytes from a proxied asset.</summary>
    /// <param name="token">The token the plugin issued.</param>
    /// <param name="range">The range, or <see langword="null"/> for everything.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The bytes; <see cref="ErrorCode.NotFound"/> for an unknown token.</returns>
    Task<PluginCall<Chunk>> ReadAsync(string token, ByteRange? range, CancellationToken cancellationToken);

    /// <summary>Suggests completions for partly typed search text (extension <see cref="PluginExtensions.SearchAssist"/>).</summary>
    /// <param name="text">The partial text.</param>
    /// <param name="limit">The most suggestions wanted.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The suggestions; <see cref="ErrorCode.Unsupported"/> when the extension is not offered.</returns>
    Task<PluginCall<IReadOnlyList<string>>> SuggestAsync(string text, int limit, CancellationToken cancellationToken);

    /// <summary>Adds detail to nodes the host already holds (extension <see cref="PluginExtensions.SearchAssist"/>).</summary>
    /// <param name="ids">The plugin-local ids.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The enriched nodes; <see cref="ErrorCode.Unsupported"/> when the extension is not offered.</returns>
    Task<PluginCall<IReadOnlyList<PluginNode>>> EnrichAsync(IReadOnlyList<LocalNodeId> ids, CancellationToken cancellationToken);
}
