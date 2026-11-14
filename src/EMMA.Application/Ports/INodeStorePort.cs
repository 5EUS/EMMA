using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>Persists nodes the host has seen, so clients and later calls can find them without asking the plugin again.</summary>
public interface INodeStorePort
{
    /// <summary>Inserts or replaces nodes.</summary>
    /// <param name="nodes">The validated nodes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the nodes are stored.</returns>
    Task UpsertAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces nodes the plugin described in full (an answer to <c>get</c>), so later reads can trust them.</summary>
    /// <param name="nodes">The validated nodes.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the nodes are stored.</returns>
    Task UpsertDetailedAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken);

    /// <summary>Finds a node.</summary>
    /// <param name="node">The node reference.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The node, or <see langword="null"/> when never seen.</returns>
    Task<Node?> GetAsync(NodeRef node, CancellationToken cancellationToken);

    /// <summary>Finds several nodes at once.</summary>
    /// <param name="nodes">The node references; they may belong to different plugins.</param>
    /// <param name="detailedOnly">Skip nodes the host only saw in a listing or search, whose props may be incomplete.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The nodes found, in the order requested; references the host has not seen (or not in detail) are omitted.</returns>
    Task<IReadOnlyList<Node>> GetManyAsync(IReadOnlyList<NodeRef> nodes, bool detailedOnly, CancellationToken cancellationToken);

    /// <summary>Lists the ids of the children the host has stored for a node, so a refresh can tell which children are new.</summary>
    /// <param name="parent">The parent node.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored children's ids; empty when the host has never listed the node's children.</returns>
    Task<IReadOnlySet<LocalNodeId>> ChildIdsAsync(NodeRef parent, CancellationToken cancellationToken);
}
