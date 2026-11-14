using System.Collections.Concurrent;

using EMMA.Application.Ports;

namespace EMMA.TestSupport;

/// <summary>An in-memory <see cref="IAssetCacheIndexPort"/>, shared between cache instances to model a restart.</summary>
public sealed class FakeAssetCacheIndex : IAssetCacheIndexPort
{
    private readonly ConcurrentDictionary<string, AssetCacheRecord> records = new(StringComparer.Ordinal);

    /// <summary>Gets the keys currently remembered.</summary>
    public IReadOnlyCollection<string> Keys => [.. this.records.Keys];

    /// <inheritdoc/>
    public Task<IReadOnlyList<AssetCacheRecord>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AssetCacheRecord>>([.. this.records.Values]);

    /// <inheritdoc/>
    public Task UpsertAsync(AssetCacheRecord record, CancellationToken cancellationToken)
    {
        this.records[record.Key] = record;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task TouchAsync(IReadOnlyDictionary<string, DateTimeOffset> lastUsed, CancellationToken cancellationToken)
    {
        foreach ((string key, DateTimeOffset when) in lastUsed)
        {
            if (this.records.TryGetValue(key, out AssetCacheRecord? record))
            {
                this.records[key] = record with { LastUsedAt = when };
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        foreach (string key in keys)
        {
            this.records.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        this.records.Clear();
        return Task.CompletedTask;
    }
}
