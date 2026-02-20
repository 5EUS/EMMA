using System.Collections.Concurrent;
using EMMA.Application.Ports;

namespace EMMA.Infrastructure.InMemory;

/// <summary>
/// In-memory cache adapter for pipeline-level results.
/// </summary>
public sealed class InMemoryCachePort : ICachePort
{
    private sealed record CacheEntry(object Value, DateTimeOffset ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();

    /// <summary>
    /// Retrieves a cached value or null if missing/expired.
    /// </summary>
    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _entries.TryRemove(key, out _);
                return Task.FromResult<T?>(null);
            }

            return Task.FromResult(entry.Value as T);
        }

        return Task.FromResult<T?>(null);
    }

    /// <summary>
    /// Stores a value with a time-to-live.
    /// </summary>
    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
        _entries[key] = new CacheEntry(value, expiresAt);
        return Task.CompletedTask;
    }
}
