using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Runtime.Tests;

/// <summary>Tests the caches end to end: thumbnails and child listings survive a restart, and refresh and clear empty them.</summary>
public sealed class CacheTests
{
    private static readonly NodeKind Series = NodeKind.Create("series");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A thumbnail fetched once is served from disk by the next runtime over the same directory.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ThumbnailsSurviveARestart()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            FakeAssetFetcher firstFetcher = Fetcher();
            EmmaRuntime first = Open(host, new FakePluginGateway(), firstFetcher);
            await using (first.ConfigureAwait(true))
            {
                await first.InitializeAsync(Ct).ConfigureAwait(true);
                await first.Nodes.UpsertAsync([Thumbnailed("a")], Ct).ConfigureAwait(true);
                Outcome<AssetBytes> fetched = await first.ReadThumbnailAsync(TestData.Ref("a"), Ct).ConfigureAwait(true);
                Assert.Equal([1, 2, 3, 4], fetched.Value.Bytes.ToArray());
            }

            FakeAssetFetcher secondFetcher = Fetcher();
            EmmaRuntime second = Open(host, new FakePluginGateway(), secondFetcher);
            await using (second.ConfigureAwait(true))
            {
                await second.InitializeAsync(Ct).ConfigureAwait(true);
                Outcome<AssetBytes> cached = await second.ReadThumbnailAsync(TestData.Ref("a"), Ct).ConfigureAwait(true);

                Assert.Equal([1, 2, 3, 4], cached.Value.Bytes.ToArray());
                Assert.Equal("image/png", cached.Value.Mime);
                Assert.Equal(1, firstFetcher.Calls);
                Assert.Equal(0, secondFetcher.Calls);
            }
        }
    }

    /// <summary>The cache directory is where the host says, not inside the data directory.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheCacheDirectoryIsConfigurable()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            string cache = Path.Combine(Path.GetTempPath(), $"emma-cache-dir-{Guid.NewGuid():n}");
            try
            {
                EmmaRuntime runtime = Open(host, new FakePluginGateway(), Fetcher(), options => options with { CacheDirectory = cache });
                await using (runtime.ConfigureAwait(true))
                {
                    await runtime.InitializeAsync(Ct).ConfigureAwait(true);
                    await runtime.Nodes.UpsertAsync([Thumbnailed("a")], Ct).ConfigureAwait(true);
                    await runtime.ReadThumbnailAsync(TestData.Ref("a"), Ct).ConfigureAwait(true);
                }

                Assert.Single(Directory.GetFiles(Path.Combine(cache, "assets")));
                Assert.False(Directory.Exists(Path.Combine(host.DataDirectory, "cache")));
            }
            finally
            {
                if (Directory.Exists(cache))
                {
                    Directory.Delete(cache, recursive: true);
                }
            }
        }
    }

    /// <summary>A complete child listing is served after a restart without asking the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ChildListingsSurviveARestart()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            FakePluginGateway firstGateway = ChaptersGateway();
            EmmaRuntime first = Open(host, firstGateway, Fetcher());
            await using (first.ConfigureAwait(true))
            {
                await first.InitializeAsync(Ct).ConfigureAwait(true);
                await first.Nodes.UpsertAsync([Thumbnailed("s")], Ct).ConfigureAwait(true);
                await first.Catalog.ChildrenAsync(TestData.Ref("s"), new PageRequest(), Ct).ConfigureAwait(true);
            }

            FakePluginGateway secondGateway = ChaptersGateway();
            EmmaRuntime second = Open(host, secondGateway, Fetcher());
            await using (second.ConfigureAwait(true))
            {
                await second.InitializeAsync(Ct).ConfigureAwait(true);
                Outcome<NodePage> page = await second.Catalog.ChildrenAsync(TestData.Ref("s"), new PageRequest(), Ct).ConfigureAwait(true);

                Assert.Equal(["c1", "c2"], page.Value.Items.Select(node => node.Ref.Id.Value));
                Assert.Equal(1, firstGateway.ChildrenCalls);
                Assert.Equal(0, secondGateway.ChildrenCalls);
            }
        }
    }

    /// <summary>Refreshing a node drops its cached thumbnail, so the next read fetches it again.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RefreshingANodeDropsItsCachedThumbnail()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            FakePluginGateway gateway = new()
            {
                OnGet = (_, ids) => Outcome.Success<IReadOnlyList<PluginNode>>([.. ids.Select(id => new PluginNode(id.Value, "series", "Title", null, "https://img.example.com/t.png", []))]),
            };
            FakeAssetFetcher fetcher = Fetcher();
            EmmaRuntime runtime = Open(host, gateway, fetcher);
            await using (runtime.ConfigureAwait(true))
            {
                await runtime.InitializeAsync(Ct).ConfigureAwait(true);
                await runtime.Nodes.UpsertAsync([Thumbnailed("a")], Ct).ConfigureAwait(true);
                await runtime.ReadThumbnailAsync(TestData.Ref("a"), Ct).ConfigureAwait(true);

                await runtime.Refresh.RefreshNodeAsync(TestData.Ref("a"), Ct).ConfigureAwait(true);
                await runtime.ReadThumbnailAsync(TestData.Ref("a"), Ct).ConfigureAwait(true);

                Assert.Equal(2, fetcher.Calls);
            }
        }
    }

    /// <summary>Clearing the cache empties thumbnails and listings but keeps the nodes the host has seen.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ClearingTheCacheKeepsTheNodes()
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            FakePluginGateway gateway = ChaptersGateway();
            FakeAssetFetcher fetcher = Fetcher();
            EmmaRuntime runtime = Open(host, gateway, fetcher);
            await using (runtime.ConfigureAwait(true))
            {
                await runtime.InitializeAsync(Ct).ConfigureAwait(true);
                await runtime.Nodes.UpsertAsync([Thumbnailed("s")], Ct).ConfigureAwait(true);
                await runtime.ReadThumbnailAsync(TestData.Ref("s"), Ct).ConfigureAwait(true);
                await runtime.Catalog.ChildrenAsync(TestData.Ref("s"), new PageRequest(), Ct).ConfigureAwait(true);

                await runtime.ClearCacheAsync(Ct).ConfigureAwait(true);
                await runtime.ReadThumbnailAsync(TestData.Ref("s"), Ct).ConfigureAwait(true);
                await runtime.Catalog.ChildrenAsync(TestData.Ref("s"), new PageRequest(), Ct).ConfigureAwait(true);

                Assert.Equal(2, fetcher.Calls);
                Assert.Equal(2, gateway.ChildrenCalls);
                Assert.NotNull(await runtime.Nodes.GetAsync(TestData.Ref("c1"), Ct).ConfigureAwait(true));
            }
        }
    }

    private static FakeAssetFetcher Fetcher() => new(new FakeTimeProvider(TestPki.Now));

    private static Node Thumbnailed(string id) =>
        new(TestData.Ref(id), Series, $"Title {id}", null, TestData.Url("img.example.com", "/t.png"), []);

    private static FakePluginGateway ChaptersGateway() => new()
    {
        OnChildren = (_, parent, _) => Outcome.Success(TestData.RawPage(null, TestData.RawNode("c1", "chapter", parent: parent?.Value), TestData.RawNode("c2", "chapter", parent: parent?.Value))),
    };

    private static EmmaRuntime Open(RuntimeHarness host, FakePluginGateway gateway, FakeAssetFetcher fetcher, Func<EmmaRuntimeOptions, EmmaRuntimeOptions>? adjust = null) =>
        host.Open(options => (adjust?.Invoke(options) ?? options) with
        {
            GatewayOverride = gateway,
            FetcherOverride = fetcher,
            CallOptions = new PluginCallOptions(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(30)),
        });
}
