using System.Collections.Concurrent;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>A listing store that forgets everything on exit. For tests and development; SQLite is the real store.</summary>
public sealed class InMemoryNodeListingStore : INodeListingPort
{
    private readonly ConcurrentDictionary<NodeRef, IReadOnlyList<LocalNodeId>> listings = new();

    /// <inheritdoc/>
    public Task<StoredListing?> GetPageAsync(NodeRef parent, int offset, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoredListing? page = this.listings.TryGetValue(parent, out IReadOnlyList<LocalNodeId>? children)
            ? new StoredListing([.. children.Skip(offset).Take(limit)], children.Count)
            : null;
        return Task.FromResult(page);
    }

    /// <inheritdoc/>
    public Task ReplaceAsync(NodeRef parent, IReadOnlyList<LocalNodeId> children, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.listings[parent] = [.. children];
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.listings.Clear();
        return Task.CompletedTask;
    }
}
