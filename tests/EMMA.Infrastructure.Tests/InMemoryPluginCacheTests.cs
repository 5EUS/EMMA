using EMMA.Infrastructure.InMemory;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Infrastructure.Tests;

/// <summary>Tests for <see cref="InMemoryPluginCache"/>.</summary>
public sealed class InMemoryPluginCacheTests
{
    private readonly FakeTimeProvider time = new();
    private readonly InMemoryPluginCache cache;

    /// <summary>Initializes a new instance of the <see cref="InMemoryPluginCacheTests"/> class.</summary>
    public InMemoryPluginCacheTests() => this.cache = new InMemoryPluginCache(this.time);

    /// <summary>A stored value is returned until it expires.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ValuesExpire()
    {
        await this.cache.SetAsync(TestData.Plugin, "k", new byte[] { 1 }, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(true);

        Assert.NotNull(await this.cache.GetAsync(TestData.Plugin, "k", CancellationToken.None).ConfigureAwait(true));

        this.time.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(await this.cache.GetAsync(TestData.Plugin, "k", CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>Two plugins using the same key never see each other's data.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PluginsAreIsolated()
    {
        await this.cache.SetAsync(TestData.Plugin, "k", new byte[] { 1 }, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(true);

        Assert.Null(await this.cache.GetAsync(TestData.OtherPlugin, "k", CancellationToken.None).ConfigureAwait(true));
    }

    /// <summary>The cache keeps its own copy of the bytes.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task StoredBytesAreCopied()
    {
        byte[] bytes = [1, 2];
        await this.cache.SetAsync(TestData.Plugin, "k", bytes, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(true);

        bytes[0] = 9;

        Assert.Equal([1, 2], (await this.cache.GetAsync(TestData.Plugin, "k", CancellationToken.None).ConfigureAwait(true))?.ToArray());
    }

    /// <summary>Writing again replaces the value and restarts the clock.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task WritingAgainReplaces()
    {
        await this.cache.SetAsync(TestData.Plugin, "k", new byte[] { 1 }, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromSeconds(50));
        await this.cache.SetAsync(TestData.Plugin, "k", new byte[] { 2 }, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(true);
        this.time.Advance(TimeSpan.FromSeconds(50));

        Assert.Equal([2], (await this.cache.GetAsync(TestData.Plugin, "k", CancellationToken.None).ConfigureAwait(true))?.ToArray());
    }

    /// <summary>Over-long keys, over-large values and non-positive lifetimes are rejected.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task LimitsAreEnforced()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this.cache.SetAsync(TestData.Plugin, new string('k', InMemoryPluginCache.MaxKeyLength + 1), new byte[1], TimeSpan.FromMinutes(1), CancellationToken.None)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => this.cache.SetAsync(TestData.Plugin, "k", new byte[InMemoryPluginCache.MaxValueBytes + 1], TimeSpan.FromMinutes(1), CancellationToken.None)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => this.cache.SetAsync(TestData.Plugin, "k", new byte[1], TimeSpan.Zero, CancellationToken.None)).ConfigureAwait(true);
        await Assert.ThrowsAsync<ArgumentException>(() => this.cache.SetAsync(TestData.Plugin, string.Empty, new byte[1], TimeSpan.FromMinutes(1), CancellationToken.None)).ConfigureAwait(true);
    }
}
