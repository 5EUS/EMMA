using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Cache;

/// <summary>
/// An in-memory, least-recently-used cache of resolutions with a per-entry expiry. It never touches disk: a resolution
/// can hold cookies or tokens, and none of it is worth keeping past the process.
/// </summary>
/// <param name="maxEntries">The most resolutions kept.</param>
/// <param name="time">The time source used for expiry.</param>
public sealed class BoundedResolutionCache(int maxEntries, TimeProvider time) : IResolutionCachePort
{
    private readonly Lock gate = new();
    private readonly Dictionary<ResolutionKey, Entry> entries = [];
    private readonly LinkedList<Entry> recency = new();

    /// <inheritdoc/>
    public Task<IReadOnlyList<Asset>?> GetAsync(ResolutionKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(key, out Entry? entry))
            {
                return Task.FromResult<IReadOnlyList<Asset>?>(null);
            }

            if (entry.ExpiresAt <= time.GetUtcNow())
            {
                this.Remove(entry);
                return Task.FromResult<IReadOnlyList<Asset>?>(null);
            }

            this.recency.Remove(entry.Node!);
            entry.Node = this.recency.AddFirst(entry);
            return Task.FromResult<IReadOnlyList<Asset>?>(entry.Assets);
        }
    }

    /// <inheritdoc/>
    public Task SetAsync(ResolutionKey key, IReadOnlyList<Asset> assets, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxEntries <= 0)
        {
            return Task.CompletedTask;
        }

        lock (this.gate)
        {
            if (this.entries.TryGetValue(key, out Entry? existing))
            {
                this.Remove(existing);
            }

            Entry entry = new(key, assets, expiresAt);
            this.entries[key] = entry;
            entry.Node = this.recency.AddFirst(entry);
            while (this.entries.Count > maxEntries && this.recency.Last is { } oldest)
            {
                this.Remove(oldest.Value);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveNodeAsync(NodeRef node, CancellationToken cancellationToken) =>
        this.RemoveWhere(entry => entry.Key.Node == node, cancellationToken);

    /// <inheritdoc/>
    public Task RemovePluginAsync(PluginId plugin, CancellationToken cancellationToken) =>
        this.RemoveWhere(entry => entry.Key.Node.Plugin == plugin, cancellationToken);

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken) =>
        this.RemoveWhere(_ => true, cancellationToken);

    private Task RemoveWhere(Func<Entry, bool> predicate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (this.gate)
        {
            foreach (Entry entry in this.entries.Values.Where(predicate).ToList())
            {
                this.Remove(entry);
            }
        }

        return Task.CompletedTask;
    }

    private void Remove(Entry entry)
    {
        this.entries.Remove(entry.Key);
        if (entry.Node is { List: not null } node)
        {
            this.recency.Remove(node);
        }
    }

    private sealed class Entry(ResolutionKey key, IReadOnlyList<Asset> assets, DateTimeOffset expiresAt)
    {
        public ResolutionKey Key { get; } = key;

        public IReadOnlyList<Asset> Assets { get; } = assets;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public LinkedListNode<Entry>? Node { get; set; }
    }
}
