using System.Collections.Concurrent;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.InMemory;

/// <summary>The in-memory cache behind <c>host.cache-get</c> and <c>host.cache-put</c>: private per plugin, expiring on a controllable clock.</summary>
/// <param name="time">The time source used for expiry.</param>
public sealed class InMemoryPluginCache(TimeProvider time) : IPluginCachePort
{
    /// <summary>The longest key accepted, in characters.</summary>
    public const int MaxKeyLength = 256;

    /// <summary>The largest value accepted, in bytes.</summary>
    public const int MaxValueBytes = 1024 * 1024;

    private readonly ConcurrentDictionary<(PluginId Plugin, string Key), Entry> entries = new();

    /// <inheritdoc/>
    public Task<ReadOnlyMemory<byte>?> GetAsync(PluginId plugin, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (this.entries.TryGetValue((plugin, key), out Entry? entry))
        {
            if (entry.ExpiresAt > time.GetUtcNow())
            {
                return Task.FromResult<ReadOnlyMemory<byte>?>(entry.Value);
            }

            this.entries.TryRemove(new KeyValuePair<(PluginId Plugin, string Key), Entry>((plugin, key), entry));
        }

        return Task.FromResult<ReadOnlyMemory<byte>?>(null);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The key or value is over the limit, or the time to live is not positive.</exception>
    public Task SetAsync(
        PluginId plugin,
        string key,
        ReadOnlyMemory<byte> value,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (key.Length > MaxKeyLength)
        {
            throw new ArgumentException($"A cache key is at most {MaxKeyLength} characters.", nameof(key));
        }

        if (value.Length > MaxValueBytes)
        {
            throw new ArgumentException($"A cache value is at most {MaxValueBytes} bytes.", nameof(value));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        this.entries[(plugin, key)] = new Entry(value.ToArray(), time.GetUtcNow() + timeToLive);
        return Task.CompletedTask;
    }

    private sealed record Entry(byte[] Value, DateTimeOffset ExpiresAt);
}
