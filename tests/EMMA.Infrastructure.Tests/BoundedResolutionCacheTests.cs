using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Infrastructure.Tests;

/// <summary>Tests for <see cref="BoundedResolutionCache"/>.</summary>
public sealed class BoundedResolutionCacheTests
{
    private static readonly AssetRole Image = AssetRole.Create("image");

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stored resolution comes back until it expires; an unknown key is a miss.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ServesUntilItExpires()
    {
        BoundedResolutionCache cache = new(8, this.time);
        await cache.SetAsync(Key("a"), Assets("a"), this.time.GetUtcNow().AddMinutes(1), Ct).ConfigureAwait(true);

        Assert.NotNull(await cache.GetAsync(Key("a"), Ct).ConfigureAwait(true));
        Assert.Null(await cache.GetAsync(Key("missing"), Ct).ConfigureAwait(true));
        this.time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await cache.GetAsync(Key("a"), Ct).ConfigureAwait(true));
    }

    /// <summary>When full, the least recently used entry goes first, and reading counts as use.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task EvictsTheLeastRecentlyUsed()
    {
        BoundedResolutionCache cache = new(2, this.time);
        DateTimeOffset until = this.time.GetUtcNow().AddMinutes(1);
        await cache.SetAsync(Key("a"), Assets("a"), until, Ct).ConfigureAwait(true);
        await cache.SetAsync(Key("b"), Assets("b"), until, Ct).ConfigureAwait(true);
        await cache.GetAsync(Key("a"), Ct).ConfigureAwait(true);

        await cache.SetAsync(Key("c"), Assets("c"), until, Ct).ConfigureAwait(true);

        Assert.NotNull(await cache.GetAsync(Key("a"), Ct).ConfigureAwait(true));
        Assert.Null(await cache.GetAsync(Key("b"), Ct).ConfigureAwait(true));
        Assert.NotNull(await cache.GetAsync(Key("c"), Ct).ConfigureAwait(true));
    }

    /// <summary>Writing a key again replaces it, and a size of zero keeps nothing.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ReplacesAKeyAndZeroKeepsNothing()
    {
        BoundedResolutionCache cache = new(8, this.time);
        DateTimeOffset until = this.time.GetUtcNow().AddMinutes(1);
        await cache.SetAsync(Key("a"), Assets("old"), until, Ct).ConfigureAwait(true);
        await cache.SetAsync(Key("a"), Assets("new"), until, Ct).ConfigureAwait(true);
        BoundedResolutionCache off = new(0, this.time);
        await off.SetAsync(Key("a"), Assets("a"), until, Ct).ConfigureAwait(true);

        Assert.Equal("new", (await cache.GetAsync(Key("a"), Ct).ConfigureAwait(true))![0].Mime);
        Assert.Null(await off.GetAsync(Key("a"), Ct).ConfigureAwait(true));
    }

    /// <summary>A node, a plugin or everything can be dropped, and nothing else is.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RemovesByNodePluginOrAll()
    {
        BoundedResolutionCache cache = new(8, this.time);
        DateTimeOffset until = this.time.GetUtcNow().AddMinutes(1);
        ResolutionKey mine = Key("a");
        ResolutionKey sibling = Key("b");
        ResolutionKey other = new(new NodeRef(TestData.OtherPlugin, LocalNodeId.Create("a")), Image, string.Empty);
        foreach (ResolutionKey key in new[] { mine, sibling, other })
        {
            await cache.SetAsync(key, Assets("x"), until, Ct).ConfigureAwait(true);
        }

        await cache.RemoveNodeAsync(mine.Node, Ct).ConfigureAwait(true);
        Assert.Null(await cache.GetAsync(mine, Ct).ConfigureAwait(true));
        Assert.NotNull(await cache.GetAsync(sibling, Ct).ConfigureAwait(true));

        await cache.RemovePluginAsync(TestData.Plugin, Ct).ConfigureAwait(true);
        Assert.Null(await cache.GetAsync(sibling, Ct).ConfigureAwait(true));
        Assert.NotNull(await cache.GetAsync(other, Ct).ConfigureAwait(true));

        await cache.ClearAsync(Ct).ConfigureAwait(true);
        Assert.Null(await cache.GetAsync(other, Ct).ConfigureAwait(true));
    }

    private static ResolutionKey Key(string node) => ResolutionKey.For(TestData.Ref(node), Image, []);

    private static IReadOnlyList<Asset> Assets(string mime) => [new Asset(mime, new ProxiedDelivery("t"), null, [])];
}
