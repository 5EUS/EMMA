using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Infrastructure.Tests;

/// <summary>Tests for <see cref="BoundedAssetCache"/>, against a real temporary directory.</summary>
public sealed class BoundedAssetCacheTests : IDisposable
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("emma-cache-test-");

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose() => this.directory.Delete(recursive: true);

    /// <summary>A stored asset comes back; an unknown key is a miss.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StoresAndReturnsFromMemory()
    {
        BoundedAssetCache cache = this.Cache(memory: 100, disk: 0);

        await cache.SetAsync(Key("a"), this.Payload(1, 2, 3), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal([1, 2, 3], (await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true))?.Payload.ToArray());
        Assert.Null(await cache.GetAsync(Key("missing"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>Changing the caller's buffer after storing does not change what the cache holds.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CallerCannotCorruptCachedBytes()
    {
        BoundedAssetCache cache = this.Cache(memory: 100, disk: 0);
        byte[] bytes = [1, 2, 3];

        await cache.SetAsync(Key("a"), this.Payload(bytes), CancellationToken.None).ConfigureAwait(true);
        bytes[0] = 99;

        Assert.Equal([1, 2, 3], (await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true))?.Payload.ToArray());
    }

    /// <summary>With no disk tier, the least recently used entry is evicted first, and reading counts as use.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EvictsTheLeastRecentlyUsedWithoutADiskTier()
    {
        BoundedAssetCache cache = this.Cache(memory: 10, disk: 0);
        await cache.SetAsync(Key("a"), this.Payload(new byte[5]), CancellationToken.None).ConfigureAwait(true);
        await cache.SetAsync(Key("b"), this.Payload(new byte[5]), CancellationToken.None).ConfigureAwait(true);
        await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true);

        await cache.SetAsync(Key("c"), this.Payload(new byte[5]), CancellationToken.None).ConfigureAwait(true);

        Assert.NotNull(await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true));
        Assert.Null(await cache.GetAsync(Key("b"), CancellationToken.None).ConfigureAwait(true));
        Assert.NotNull(await cache.GetAsync(Key("c"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>Entries pushed out of memory spill to disk and are still readable.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SpillsToDiskAndReadsBack()
    {
        BoundedAssetCache cache = this.Cache(memory: 6, disk: 1000);

        await cache.SetAsync(Key("a"), this.Payload(1, 1, 1, 1, 1), CancellationToken.None).ConfigureAwait(true);
        await cache.SetAsync(Key("b"), this.Payload(2, 2, 2, 2, 2), CancellationToken.None).ConfigureAwait(true);
        await cache.SetAsync(Key("c"), this.Payload(3, 3, 3, 3, 3), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, this.directory.GetFiles().Length);
        Assert.Equal([1, 1, 1, 1, 1], (await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true))?.Payload.ToArray());
        Assert.Equal([3, 3, 3, 3, 3], (await cache.GetAsync(Key("c"), CancellationToken.None).ConfigureAwait(true))?.Payload.ToArray());
    }

    /// <summary>An asset too big for memory goes straight to disk.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LargeAssetsGoStraightToDisk()
    {
        BoundedAssetCache cache = this.Cache(memory: 4, disk: 1000);

        await cache.SetAsync(Key("big"), this.Payload(new byte[50]), CancellationToken.None).ConfigureAwait(true);

        Assert.Single(this.directory.GetFiles());
        Assert.Equal(50, (await cache.GetAsync(Key("big"), CancellationToken.None).ConfigureAwait(true))?.Payload.Length);
    }

    /// <summary>When the disk budget is exceeded the oldest files are deleted, not just forgotten.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DiskBudgetEvictsAndDeletesFiles()
    {
        BoundedAssetCache cache = this.Cache(memory: 1, disk: 20);

        await cache.SetAsync(Key("a"), this.Payload(new byte[10]), CancellationToken.None).ConfigureAwait(true);
        await cache.SetAsync(Key("b"), this.Payload(new byte[10]), CancellationToken.None).ConfigureAwait(true);
        await cache.SetAsync(Key("c"), this.Payload(new byte[10]), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, this.directory.GetFiles().Length);
        Assert.Null(await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true));
        Assert.NotNull(await cache.GetAsync(Key("c"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>An asset larger than every tier is not stored and leaves no file behind.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task OversizedAssetsAreSkipped()
    {
        BoundedAssetCache cache = this.Cache(memory: 4, disk: 8);

        await cache.SetAsync(Key("huge"), this.Payload(new byte[100]), CancellationToken.None).ConfigureAwait(true);

        Assert.Null(await cache.GetAsync(Key("huge"), CancellationToken.None).ConfigureAwait(true));
        Assert.Empty(this.directory.GetFiles());
    }

    /// <summary>Replacing a key removes the old file instead of leaking it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReplacingAKeyDoesNotLeakFiles()
    {
        BoundedAssetCache cache = this.Cache(memory: 1, disk: 1000);

        await cache.SetAsync(Key("a"), this.Payload(new byte[10]), CancellationToken.None).ConfigureAwait(true);
        await cache.SetAsync(Key("a"), this.Payload(new byte[12]), CancellationToken.None).ConfigureAwait(true);

        Assert.Single(this.directory.GetFiles());
        Assert.Equal(12, (await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true))?.Payload.Length);
    }

    /// <summary>Entries older than the maximum age are misses.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ExpiredEntriesAreMisses()
    {
        BoundedAssetCache cache = this.Cache(memory: 100, disk: 0);
        await cache.SetAsync(Key("a"), this.Payload(1), CancellationToken.None).ConfigureAwait(true);

        this.time.Advance(TimeSpan.FromDays(2));

        Assert.Null(await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>A file that vanished from disk is a miss, and the entry is forgotten.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task MissingFileIsAMiss()
    {
        BoundedAssetCache cache = this.Cache(memory: 1, disk: 1000);
        await cache.SetAsync(Key("a"), this.Payload(new byte[10]), CancellationToken.None).ConfigureAwait(true);
        foreach (FileInfo file in this.directory.GetFiles())
        {
            file.Delete();
        }

        Assert.Null(await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true));
        Assert.Null(await cache.GetAsync(Key("a"), CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>Files left by a previous run are removed on start.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task InitializeClearsLeftoverFiles()
    {
        await File.WriteAllBytesAsync(Path.Combine(this.directory.FullName, "orphan"), [1], CancellationToken.None).ConfigureAwait(true);
        BoundedAssetCache cache = this.Cache(memory: 1, disk: 1000);

        await cache.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

        Assert.Empty(this.directory.GetFiles());
    }

    /// <summary>Many concurrent writers and readers neither throw nor mix up payloads.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ConcurrentUseKeepsPayloadsIntact()
    {
        BoundedAssetCache cache = this.Cache(memory: 64, disk: 100_000);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(async i =>
        {
            byte marker = (byte)(i % 50);
            AssetKey key = Key($"k{i % 50}");
            await cache.SetAsync(key, this.Payload(Enumerable.Repeat(marker, 16).ToArray()), CancellationToken.None).ConfigureAwait(false);
            AssetPayload? read = await cache.GetAsync(key, CancellationToken.None).ConfigureAwait(false);
            if (read is not null)
            {
                Assert.All(read.Payload.ToArray(), value => Assert.Equal(marker, value));
            }
        })).ConfigureAwait(true);

        Assert.True(this.directory.GetFiles().Length <= 100_000 / 16);
    }

    private static AssetKey Key(string variant) =>
        new(TestData.Ref("n"), AssetRole.Create("image"), variant);

    private BoundedAssetCache Cache(long memory, long disk) =>
        new(new AssetCacheOptions(memory, disk, this.directory.FullName, TimeSpan.FromDays(1)), this.time);

    private AssetPayload Payload(params byte[] bytes) => new("image/png", bytes, this.time.GetUtcNow());
}
