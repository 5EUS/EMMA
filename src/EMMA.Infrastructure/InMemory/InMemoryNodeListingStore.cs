using System.Collections.Concurrent;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>A listing store that forgets everything on exit. For tests and development; SQLite is the real store.</summary>
public sealed class InMemoryNodeListingStore : INodeListingPort
{
    private readonly ConcurrentDictionary<NodeRef, (IReadOnlyList<LocalNodeId> Children, DateTimeOffset ListedAt)> listings = new();
    private readonly TimeProvider time;

    /// <summary>Initializes a new instance of the <see cref="InMemoryNodeListingStore"/> class.</summary>
    /// <param name="time">The time source that stamps each listing; the system clock when <see langword="null"/>.</param>
    public InMemoryNodeListingStore(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    /// <inheritdoc/>
    public Task<StoredListing?> GetPageAsync(NodeRef parent, int offset, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoredListing? page = this.listings.TryGetValue(parent, out (IReadOnlyList<LocalNodeId> Children, DateTimeOffset ListedAt) listing)
            ? new StoredListing([.. listing.Children.Skip(offset).Take(limit)], listing.Children.Count, listing.ListedAt)
            : null;
        return Task.FromResult(page);
    }

    /// <inheritdoc/>
    public Task ReplaceAsync(NodeRef parent, IReadOnlyList<LocalNodeId> children, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.listings[parent] = ([.. children], this.time.GetUtcNow());
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
