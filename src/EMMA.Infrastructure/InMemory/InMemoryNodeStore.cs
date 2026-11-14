using System.Collections.Concurrent;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>A node store that forgets everything on exit. For tests and development; SQLite is the real store.</summary>
public sealed class InMemoryNodeStore : INodeStorePort
{
    private readonly ConcurrentDictionary<NodeRef, Node> nodes = new();
    private readonly ConcurrentDictionary<NodeRef, bool> detailed = new();

    /// <inheritdoc/>
    public Task UpsertAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken) => this.StoreAsync(nodes, isDetailed: false, cancellationToken);

    /// <inheritdoc/>
    public Task UpsertDetailedAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken) => this.StoreAsync(nodes, isDetailed: true, cancellationToken);

    /// <inheritdoc/>
    public Task<Node?> GetAsync(NodeRef node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.nodes.GetValueOrDefault(node));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Node>> GetManyAsync(IReadOnlyList<NodeRef> nodes, bool detailedOnly, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Node> found =
        [
            .. nodes.Where(reference => this.nodes.ContainsKey(reference) && (!detailedOnly || this.detailed.GetValueOrDefault(reference)))
                .Select(reference => this.nodes[reference]),
        ];
        return Task.FromResult(found);
    }

    /// <inheritdoc/>
    public Task<IReadOnlySet<LocalNodeId>> ChildIdsAsync(NodeRef parent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlySet<LocalNodeId> ids = this.nodes.Values
            .Where(node => node.Parent == parent)
            .Select(node => node.Ref.Id)
            .ToHashSet();
        return Task.FromResult(ids);
    }

    private Task StoreAsync(IReadOnlyList<Node> nodes, bool isDetailed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (Node node in nodes)
        {
            // The same rule as the SQL store: a write that changes the node resets the flag; one that does not keeps it.
            bool unchanged = this.nodes.TryGetValue(node.Ref, out Node? previous) && previous == node;
            this.detailed[node.Ref] = isDetailed || (unchanged && this.detailed.GetValueOrDefault(node.Ref));
            this.nodes[node.Ref] = node;
        }

        return Task.CompletedTask;
    }
}
