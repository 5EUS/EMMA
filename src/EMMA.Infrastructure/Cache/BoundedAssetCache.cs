using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Infrastructure.Cache;

/// <summary>
/// A bounded cache of fetched asset bytes shared by every plugin: least-recently-used
/// eviction, a memory tier that spills to a disk tier, and a maximum age. All file IO
/// happens outside the lock, and only IO failures are tolerated (a failed read is a
/// cache miss; nothing else is swallowed).
/// </summary>
/// <param name="options">The budgets.</param>
/// <param name="time">The time source used for expiry.</param>
public sealed class BoundedAssetCache(AssetCacheOptions options, TimeProvider time) : IAssetCachePort
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recency = new();
    private long memoryBytes;
    private long diskBytes;

    private enum Tier
    {
        Memory,
        Spilling,
        Disk,
    }

    /// <summary>
    /// Deletes files a previous run left behind. The cache keeps no index across restarts,
    /// so anything already in the directory is unreachable. Call once at startup.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cleanup.</param>
    /// <returns>A task that completes when the directory is clear.</returns>
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (options.DiskBudgetBytes > 0 && Directory.Exists(options.DiskRoot))
        {
            foreach (string path in Directory.EnumerateFiles(options.DiskRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(path);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<AssetPayload?> GetAsync(AssetKey key, CancellationToken cancellationToken)
    {
        string text = key.ToString();
        Entry entry;
        lock (this.gate)
        {
            if (!this.entries.TryGetValue(text, out Entry? found))
            {
                return null;
            }

            if (time.GetUtcNow() - found.Payload.FetchedAt > options.MaxAge)
            {
                this.RemoveLocked(found);
                return null;
            }

            this.Touch(found);
            if (found.Tier != Tier.Disk)
            {
                return found.Payload;
            }

            entry = found;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(entry.FilePath!, cancellationToken).ConfigureAwait(false);
            return entry.Payload with { Payload = bytes };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (this.gate)
            {
                if (this.entries.TryGetValue(text, out Entry? current) && ReferenceEquals(current, entry))
                {
                    this.RemoveLocked(entry);
                }
            }

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
        AssetPayload owned = payload with { Payload = payload.Payload.ToArray() };
        string text = key.ToString();
        string? file = null;
        if (!inMemory)
        {
            file = await this.TryWriteAsync(owned.Payload, cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                return;
            }
        }

        List<Entry> toSpill;
        List<string> toDelete;
        lock (this.gate)
        {
            if (this.entries.TryGetValue(text, out Entry? existing))
            {
                toDelete = [];
                this.RemoveLocked(existing, toDelete);
            }
            else
            {
                toDelete = [];
            }

            Entry entry = new(text, owned, size, inMemory ? Tier.Memory : Tier.Disk, file);
            this.entries[text] = entry;
            entry.Node = this.recency.AddFirst(entry);
            if (inMemory)
            {
                this.memoryBytes += size;
            }
            else
            {
                this.diskBytes += size;
            }

            toSpill = this.PlanSpills();
            this.EvictDiskLocked(toDelete);
        }

        foreach (string path in toDelete)
        {
            TryDelete(path);
        }

        await this.SpillAsync(toSpill, cancellationToken).ConfigureAwait(false);
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

    private List<Entry> PlanSpills()
    {
        List<Entry> victims = [];
        long projected = this.memoryBytes;
        LinkedListNode<Entry>? node = this.recency.Last;
        while (projected > options.MemoryBudgetBytes && node is not null)
        {
            Entry candidate = node.Value;
            node = node.Previous;
            if (candidate.Tier == Tier.Memory)
            {
                candidate.Tier = Tier.Spilling;
                victims.Add(candidate);
                projected -= candidate.Size;
            }
        }

        return victims;
    }

    private async Task SpillAsync(List<Entry> victims, CancellationToken cancellationToken)
    {
        foreach (Entry victim in victims)
        {
            string? file = options.DiskBudgetBytes > 0 && victim.Size <= options.DiskBudgetBytes
                ? await this.TryWriteAsync(victim.Payload.Payload, cancellationToken).ConfigureAwait(false)
                : null;

            List<string> toDelete = [];
            lock (this.gate)
            {
                bool stillCurrent = this.entries.TryGetValue(victim.Key, out Entry? current) && ReferenceEquals(current, victim);
                if (!stillCurrent)
                {
                    if (file is not null)
                    {
                        toDelete.Add(file);
                    }
                }
                else if (file is null)
                {
                    this.RemoveLocked(victim, toDelete);
                }
                else
                {
                    this.memoryBytes -= victim.Size;
                    this.diskBytes += victim.Size;
                    victim.Tier = Tier.Disk;
                    victim.FilePath = file;
                    victim.Payload = victim.Payload with { Payload = ReadOnlyMemory<byte>.Empty };
                    this.EvictDiskLocked(toDelete);
                }
            }

            foreach (string path in toDelete)
            {
                TryDelete(path);
            }
        }
    }

    private void EvictDiskLocked(List<string> filesToDelete)
    {
        LinkedListNode<Entry>? node = this.recency.Last;
        while (this.diskBytes > options.DiskBudgetBytes && node is not null)
        {
            Entry candidate = node.Value;
            node = node.Previous;
            if (candidate.Tier == Tier.Disk)
            {
                this.RemoveLocked(candidate, filesToDelete);
            }
        }
    }

    private async Task<string?> TryWriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        string path = Path.Combine(options.DiskRoot, Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(options.DiskRoot);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return null;
        }
    }

    private void Touch(Entry entry)
    {
        this.recency.Remove(entry.Node!);
        entry.Node = this.recency.AddFirst(entry);
    }

    private void RemoveLocked(Entry entry, List<string>? filesToDelete = null)
    {
        this.entries.Remove(entry.Key);
        if (entry.Node is { List: not null } node)
        {
            this.recency.Remove(node);
        }

        if (entry.Tier == Tier.Disk)
        {
            this.diskBytes -= entry.Size;
            if (entry.FilePath is not null)
            {
                if (filesToDelete is null)
                {
                    TryDelete(entry.FilePath);
                }
                else
                {
                    filesToDelete.Add(entry.FilePath);
                }
            }
        }
        else
        {
            this.memoryBytes -= entry.Size;
        }
    }

    private sealed class Entry(string key, AssetPayload payload, long size, Tier tier, string? filePath)
    {
        public string Key { get; } = key;

        public AssetPayload Payload { get; set; } = payload;

        public long Size { get; } = size;

        public Tier Tier { get; set; } = tier;

        public string? FilePath { get; set; } = filePath;

        public LinkedListNode<Entry>? Node { get; set; }
    }
}
