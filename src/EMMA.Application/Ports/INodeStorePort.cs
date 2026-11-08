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

    /// <summary>Finds a node.</summary>
    /// <param name="node">The node reference.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The node, or <see langword="null"/> when never seen.</returns>
    Task<Node?> GetAsync(NodeRef node, CancellationToken cancellationToken);
}
