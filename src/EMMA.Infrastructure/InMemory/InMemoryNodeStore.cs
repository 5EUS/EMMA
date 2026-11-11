using System.Collections.Concurrent;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>A node store that forgets everything on exit. For tests and development; SQLite is the real store.</summary>
public sealed class InMemoryNodeStore : INodeStorePort
{
    private readonly ConcurrentDictionary<NodeRef, Node> nodes = new();

    /// <inheritdoc/>
    public Task UpsertAsync(IReadOnlyList<Node> nodes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (Node node in nodes)
        {
            this.nodes[node.Ref] = node;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Node?> GetAsync(NodeRef node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(this.nodes.GetValueOrDefault(node));
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
}
