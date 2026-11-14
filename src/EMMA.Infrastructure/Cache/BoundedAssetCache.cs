using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Cache;

/// <summary>
/// A bounded cache of fetched asset bytes shared by every plugin: least-recently-used eviction, a memory tier in front
/// of a disk tier, and a maximum age. Every entry that fits the disk budget is written to disk when it is stored, so with
/// an <see cref="IAssetCacheIndexPort"/> the cache survives a restart; the memory tier only keeps hot copies. All file IO
/// happens outside the lock, and only IO failures are tolerated (a failed read is a cache miss; nothing else is swallowed).
/// </summary>
/// <param name="options">The budgets.</param>
/// <param name="time">The time source used for expiry.</param>
/// <param name="index">Remembers the disk files across runs; <see langword="null"/> makes the cache last one run.</param>
public sealed class BoundedAssetCache(AssetCacheOptions options, TimeProvider time, IAssetCacheIndexPort? index = null) : IAssetCachePort
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recency = new();
    private Dictionary<string, DateTimeOffset> touched = new(StringComparer.Ordinal);
    private long memoryBytes;
    private long diskBytes;

    /// <summary>
    /// Reconciles the cache directory with the index: entries whose file is gone or too old are forgotten, and files no
    /// entry names are deleted. Without an index (or a disk budget) every file a previous run left is deleted, because
    /// nothing says what it holds. Call once at startup.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cleanup.</param>
    /// <returns>A task that completes when the directory and index agree.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (options.DiskBudgetBytes <= 0 || index is null)
        {
            DeleteFiles(Directory.Exists(options.DiskRoot) ? Directory.EnumerateFiles(options.DiskRoot) : [], cancellationToken);
            if (index is not null)
            {
                await index.ClearAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        IReadOnlyList<AssetCacheRecord> records = await index.LoadAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = time.GetUtcNow();
        List<string> staleKeys = [];
        HashSet<string> kept = new(StringComparer.Ordinal);
        List<string> toDelete = [];
        lock (this.gate)
        {
            foreach (AssetCacheRecord record in records.OrderBy(record => record.LastUsedAt))
            {
                string path = Path.Combine(options.DiskRoot, record.File);
                if (now - record.FetchedAt > options.MaxAge || !File.Exists(path))
                {
                    staleKeys.Add(record.Key);
                    toDelete.Add(path);
                    continue;
                }

                Entry entry = new(record.Key, record.Node, record.Mime, record.FetchedAt, record.Bytes, bytes: null, record.File);
                this.entries[record.Key] = entry;
                entry.Node = this.recency.AddFirst(entry);
                this.diskBytes += record.Bytes;
                kept.Add(record.File);
            }

            this.EvictLocked(toDelete, staleKeys);
        }

        if (Directory.Exists(options.DiskRoot))
        {
            DeleteFiles(Directory.EnumerateFiles(options.DiskRoot).Where(path => !kept.Contains(Path.GetFileName(path))), cancellationToken);
        }

        DeleteFiles(toDelete, cancellationToken);
        if (staleKeys.Count > 0)
        {
            await index.RemoveAsync(staleKeys, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<AssetPayload?> GetAsync(AssetKey key, CancellationToken cancellationToken)
    {
        string text = key.ToString();
        Entry entry;
        List<string> expiredFiles = [];
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(text, out Entry? found))
            {
                return null;
            }

            DateTimeOffset now = time.GetUtcNow();
            if (now - found.FetchedAt > options.MaxAge)
            {
                this.RemoveLocked(found, expiredFiles);
                entry = found;
            }
            else
            {
                this.Touch(found);
                this.touched[text] = now;
                if (found.Bytes is { } hot)
                {
                    return new AssetPayload(found.Mime, hot, found.FetchedAt);
                }

                entry = found;
            }
        }

        if (expiredFiles.Count > 0 || entry.File is null)
        {
            await this.ForgetAsync(expiredFiles, [text], cancellationToken).ConfigureAwait(false);
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(options.DiskRoot, entry.File!), cancellationToken).ConfigureAwait(false);
            return new AssetPayload(entry.Mime, bytes, entry.FetchedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            List<string> files = [];
            lock (this.gate)
            {
                if (this.entries.TryGetValue(text, out Entry? current) && ReferenceEquals(current, entry))
                {
                    this.RemoveLocked(entry, files);
                }
            }

            await this.ForgetAsync(files, [text], cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SetAsync(AssetKey key, AssetPayload payload, CancellationToken cancellationToken)
    {
        long size = payload.Payload.Length;
        bool inMemory = options.MemoryBudgetBytes > 0 && size <= options.MemoryBudgetBytes;
        bool onDisk = options.DiskBudgetBytes > 0 && size <= options.DiskBudgetBytes;
        if (!inMemory && !onDisk)
        {
            return;
        }

        // Own the bytes: the caller's buffer may be reused or mutated after this returns.
        byte[] owned = payload.Payload.ToArray();
        string text = key.ToString();
        string? file = onDisk ? await this.TryWriteAsync(owned, cancellationToken).ConfigureAwait(false) : null;
        if (file is null && !inMemory)
        {
            return;
        }

        List<string> toDelete = [];
        List<string> forgotten = [];
        Dictionary<string, DateTimeOffset> touches = this.Insert(new Entry(text, key.Node, payload.Mime, payload.FetchedAt, size, inMemory ? owned : null, file), toDelete, forgotten);

        DeleteFiles(toDelete, cancellationToken);
        if (index is null)
        {
            return;
        }

        await index.RemoveAsync(forgotten, cancellationToken).ConfigureAwait(false);
        if (touches.Count > 0)
        {
            await index.TouchAsync(touches, cancellationToken).ConfigureAwait(false);
        }

        if (file is not null && !forgotten.Contains(text, StringComparer.Ordinal))
        {
            await index.UpsertAsync(
                new AssetCacheRecord(text, key.Node, file, payload.Mime, size, payload.FetchedAt, time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task RemoveNodeAsync(NodeRef node, CancellationToken cancellationToken)
    {
        List<string> files = [];
        List<string> keys = [];
        lock (this.gate)
        {
            foreach (Entry entry in this.entries.Values.Where(entry => entry.Owner == node).ToList())
            {
                keys.Add(entry.Key);
                this.RemoveLocked(entry, files);
            }
        }

        return this.ForgetAsync(files, keys, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        List<string> files = [];
        lock (this.gate)
        {
            foreach (Entry entry in this.entries.Values.ToList())
            {
                this.RemoveLocked(entry, files);
            }

            this.touched.Clear();
        }

        DeleteFiles(files, cancellationToken);
        if (index is not null)
        {
            await index.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file we cannot delete is still ours to forget; InitializeAsync retries next start.
        }
    }

    private static void DeleteFiles(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        foreach (string path in paths.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(path);
        }
    }

    /// <summary>Deletes the files of removed entries and drops their rows.</summary>
    private async Task ForgetAsync(List<string> files, List<string> keys, CancellationToken cancellationToken)
    {
        DeleteFiles(files, cancellationToken);
        if (index is not null && keys.Count > 0)
        {
            await index.RemoveAsync(keys, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> TryWriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        string name = Guid.NewGuid().ToString("n");
        string path = Path.Combine(options.DiskRoot, name);
        try
        {
            Directory.CreateDirectory(options.DiskRoot);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return null;
        }
    }

    /// <summary>Brings both tiers within budget, least recently used first. A memory-only entry leaves when memory is full.</summary>
    private void EvictLocked(List<string> filesToDelete, List<string> forgottenKeys)
    {
        LinkedListNode<Entry>? node = this.recency.Last;
        while (this.memoryBytes > options.MemoryBudgetBytes && node is not null)
        {
            Entry candidate = node.Value;
            node = node.Previous;
            if (candidate.Bytes is null)
            {
                continue;
            }

            if (candidate.File is null)
            {
                forgottenKeys.Add(candidate.Key);
                this.RemoveLocked(candidate, filesToDelete);
            }
            else
            {
                this.memoryBytes -= candidate.Size;
                candidate.Bytes = null;
            }
        }

        node = this.recency.Last;
        while (this.diskBytes > options.DiskBudgetBytes && node is not null)
        {
            Entry candidate = node.Value;
            node = node.Previous;
            if (candidate.File is not null)
            {
                forgottenKeys.Add(candidate.Key);
                this.RemoveLocked(candidate, filesToDelete);
            }
        }
    }

    /// <summary>Adds an entry, evicts what no longer fits, and hands back the pending last-use times to flush.</summary>
    private Dictionary<string, DateTimeOffset> Insert(Entry entry, List<string> filesToDelete, List<string> forgottenKeys)
    {
        lock (this.gate)
        {
            if (this.entries.TryGetValue(entry.Key, out Entry? existing))
            {
                this.RemoveLocked(existing, filesToDelete);
            }

            this.entries[entry.Key] = entry;
            entry.Node = this.recency.AddFirst(entry);
            if (entry.Bytes is not null)
            {
                this.memoryBytes += entry.Size;
            }

            if (entry.File is not null)
            {
                this.diskBytes += entry.Size;
            }

            this.EvictLocked(filesToDelete, forgottenKeys);
            Dictionary<string, DateTimeOffset> touches = this.touched;
            this.touched = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            return touches;
        }
    }

    private void Touch(Entry entry)
    {
        this.recency.Remove(entry.Node!);
        entry.Node = this.recency.AddFirst(entry);
    }

    private void RemoveLocked(Entry entry, List<string> filesToDelete)
    {
        this.entries.Remove(entry.Key);
        if (entry.Node is { List: not null } node)
        {
            this.recency.Remove(node);
        }

        if (entry.Bytes is not null)
        {
            this.memoryBytes -= entry.Size;
        }

        if (entry.File is not null)
        {
            this.diskBytes -= entry.Size;
            filesToDelete.Add(Path.Combine(options.DiskRoot, entry.File));
        }

        this.touched.Remove(entry.Key);
    }

    private sealed class Entry(string key, NodeRef owner, string mime, DateTimeOffset fetchedAt, long size, byte[]? bytes, string? file)
    {
        public string Key { get; } = key;

        public NodeRef Owner { get; } = owner;

        public string Mime { get; } = mime;

        public DateTimeOffset FetchedAt { get; } = fetchedAt;

        public long Size { get; } = size;

        public byte[]? Bytes { get; set; } = bytes;

        public string? File { get; } = file;

        public LinkedListNode<Entry>? Node { get; set; }
    }
}
