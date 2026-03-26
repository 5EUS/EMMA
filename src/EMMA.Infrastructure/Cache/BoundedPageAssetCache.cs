using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Cache;

/// <summary>
/// Bounded cache for raw page assets with LRU eviction and disk spill.
/// </summary>
public sealed class BoundedPageAssetCache(PageAssetCacheOptions? options = null) : IPageAssetCachePort
{
    private sealed class CacheEntry
    {
        public CacheEntry(
            string key,
            string contentType,
            byte[]? payload,
            string? filePath,
            DateTimeOffset fetchedAtUtc,
            long sizeBytes,
            LinkedListNode<string> lruNode)
        {
            Key = key;
            ContentType = contentType;
            Payload = payload;
            FilePath = filePath;
            FetchedAtUtc = fetchedAtUtc;
            SizeBytes = sizeBytes;
            LruNode = lruNode;
        }

        public string Key { get; }
        public string ContentType { get; set; }
        public byte[]? Payload { get; set; }
        public string? FilePath { get; set; }
        public DateTimeOffset FetchedAtUtc { get; set; }
        public long SizeBytes { get; set; }
        public LinkedListNode<string> LruNode { get; set; }

        public bool IsInMemory => Payload is not null;
        public bool IsOnDisk => FilePath is not null;
    }

    private readonly PageAssetCacheOptions _options = options ?? PageAssetCacheOptions.Default;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly object _lock = new();
    private long _memoryBytes;
    private long _diskBytes;

    public Task<MediaPageAsset?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CacheEntry? entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out entry))
            {
                return Task.FromResult<MediaPageAsset?>(null);
            }

            Touch(entry);

            if (entry.IsInMemory && entry.Payload is not null)
            {
                return Task.FromResult<MediaPageAsset?>(new MediaPageAsset(
                    entry.ContentType,
                    entry.Payload,
                    entry.FetchedAtUtc));
            }
        }

        if (entry?.FilePath is null)
        {
            return Task.FromResult<MediaPageAsset?>(null);
        }

        byte[] payload;
        try
        {
            payload = File.ReadAllBytes(entry.FilePath);
        }
        catch
        {
            lock (_lock)
            {
                if (entry is not null
                    && _entries.TryGetValue(entry.Key, out var existing)
                    && ReferenceEquals(existing, entry))
                {
                    RemoveEntry(entry);
                }
            }

            return Task.FromResult<MediaPageAsset?>(null);
        }

        return Task.FromResult<MediaPageAsset?>(new MediaPageAsset(
            entry.ContentType,
            payload,
            entry.FetchedAtUtc));
    }

    public Task SetAsync(string key, MediaPageAsset asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = asset.Payload ?? Array.Empty<byte>();
        var sizeBytes = payload.LongLength;
        var storeInMemory = _options.MemoryBudgetBytes > 0 && sizeBytes <= _options.MemoryBudgetBytes;
        var storeOnDisk = _options.DiskBudgetBytes > 0;

        if (!storeInMemory && !storeOnDisk)
        {
            return Task.CompletedTask;
        }

        string? filePath = null;
        if (!storeInMemory && storeOnDisk)
        {
            try
            {
                filePath = WriteToDisk(payload);
            }
            catch
            {
                return Task.CompletedTask;
            }
        }

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                RemoveEntry(existing);
            }

            var node = _lru.AddFirst(key);
            var entry = new CacheEntry(
                key,
                asset.ContentType,
                storeInMemory ? payload : null,
                filePath,
                asset.FetchedAtUtc,
                sizeBytes,
                node);

            _entries[key] = entry;

            if (entry.IsInMemory)
            {
                _memoryBytes += sizeBytes;
            }
            else if (entry.IsOnDisk)
            {
                _diskBytes += sizeBytes;
            }

            EnforceBudgets();
        }

        return Task.CompletedTask;
    }

    private void EnforceBudgets()
    {
        if (_options.MemoryBudgetBytes > 0)
        {
            while (_memoryBytes > _options.MemoryBudgetBytes)
            {
                var candidate = FindLruEntry(inMemoryOnly: true);
                if (candidate is null)
                {
                    break;
                }

                if (_options.DiskBudgetBytes > 0)
                {
                    SpillToDisk(candidate);
                    continue;
                }

                RemoveEntry(candidate);
            }
        }

        if (_options.DiskBudgetBytes > 0)
        {
            while (_diskBytes > _options.DiskBudgetBytes)
            {
                var candidate = FindLruEntry(inMemoryOnly: false);
                if (candidate is null)
                {
                    break;
                }

                RemoveEntry(candidate);
            }
        }
        else
        {
            while (_diskBytes > 0)
            {
                var candidate = FindLruEntry(inMemoryOnly: false);
                if (candidate is null)
                {
                    break;
                }

                RemoveEntry(candidate);
            }
        }
    }

    private CacheEntry? FindLruEntry(bool inMemoryOnly)
    {
        var node = _lru.Last;
        while (node is not null)
        {
            if (_entries.TryGetValue(node.Value, out var entry))
            {
                if (!inMemoryOnly || entry.IsInMemory)
                {
                    return entry;
                }
            }

            node = node.Previous;
        }

        return null;
    }

    private void SpillToDisk(CacheEntry entry)
    {
        if (!entry.IsInMemory || entry.Payload is null)
        {
            return;
        }

        try
        {
            var filePath = WriteToDisk(entry.Payload);
            entry.FilePath = filePath;
            entry.Payload = null;

            _memoryBytes -= entry.SizeBytes;
            _diskBytes += entry.SizeBytes;
        }
        catch
        {
            RemoveEntry(entry);
        }
    }

    private string WriteToDisk(byte[] payload)
    {
        Directory.CreateDirectory(_options.DiskRootDirectory);
        var filePath = Path.Combine(_options.DiskRootDirectory, Guid.NewGuid().ToString("n"));
        File.WriteAllBytes(filePath, payload);
        return filePath;
    }

    private void Touch(CacheEntry entry)
    {
        _lru.Remove(entry.LruNode);
        entry.LruNode = _lru.AddFirst(entry.Key);
    }

    private void RemoveEntry(CacheEntry entry)
    {
        _entries.Remove(entry.Key);
        if (entry.LruNode.List == _lru)
        {
            _lru.Remove(entry.LruNode);
        }

        if (entry.IsInMemory)
        {
            _memoryBytes -= entry.SizeBytes;
        }

        if (entry.IsOnDisk && entry.FilePath is not null)
        {
            _diskBytes -= entry.SizeBytes;
            TryDelete(entry.FilePath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
