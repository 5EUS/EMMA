using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.InMemory;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="AssetPipeline"/>.</summary>
public sealed class AssetPipelineTests
{
    private static readonly AssetRole Image = AssetRole.Create("image");

    private readonly FakeTimeProvider time = new();
    private readonly FakePluginGateway gateway = new();
    private readonly FakeAssetFetcher fetcher;
    private readonly InMemoryNodeStore store = new();
    private readonly AssetPipeline pipeline;

    /// <summary>Initializes a new instance of the <see cref="AssetPipelineTests"/> class.</summary>
    public AssetPipelineTests()
    {
        this.fetcher = new FakeAssetFetcher(this.time);
        this.pipeline = new AssetPipeline(
            this.gateway,
            new FakePolicies().Allow(TestData.Plugin, "img.example.com"),
            this.store,
            this.fetcher,
            new BoundedAssetCache(new AssetCacheOptions(1024 * 1024, 0, "unused", TimeSpan.FromDays(1)), this.time),
            new PluginCallExecutor(
                new PluginCallOptions(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)),
                this.time),
            new AssetPipelineOptions(1024),
            this.time);
    }

    /// <summary>Resolve returns the plugin's assets once they pass validation.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ResolveReturnsValidatedAssets()
    {
        this.ResolvesTo(Remote("img.example.com"));

        Outcome<IReadOnlyList<Asset>> assets = await this.pipeline.ResolveAsync(TestData.Ref("p1"), Image, [], CancellationToken.None).ConfigureAwait(true);

        Assert.Single(assets.Value);
    }

    /// <summary>An asset pointing outside the plugin's network permissions is rejected at resolve time.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ResolveRejectsHostsOutsideThePolicy()
    {
        this.ResolvesTo(Remote("evil.example.net"));

        Outcome<IReadOnlyList<Asset>> assets = await this.pipeline.ResolveAsync(TestData.Ref("p1"), Image, [], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Internal, assets.Error?.Code);
        Assert.Contains("evil.example.net", assets.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>Malformed mime types, oversized inline payloads and bad tokens are rejected.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ResolveRejectsMalformedAssets()
    {
        Asset[] bad =
        [
            new("nonsense", new ProxiedDelivery("t"), null, []),
            new("image/png", new InlineDelivery(new byte[ContractLimits.MaxInlineAssetBytes + 1]), null, []),
            new("image/png", new ProxiedDelivery(string.Empty), null, []),
        ];

        foreach (Asset asset in bad)
        {
            this.ResolvesTo(asset);
            Outcome<IReadOnlyList<Asset>> assets = await this.pipeline.ResolveAsync(TestData.Ref("p1"), Image, [], CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(ErrorCode.Internal, assets.Error?.Code);
        }
    }

    /// <summary>For a node whose kind is known, a role that kind does not declare is refused without calling the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ResolveRefusesARoleTheKindLacks()
    {
        await this.store.UpsertAsync([new Node(TestData.Ref("s1"), NodeKind.Create("series"), "S", null, null, [])], CancellationToken.None).ConfigureAwait(true);

        Outcome<IReadOnlyList<Asset>> assets = await this.pipeline.ResolveAsync(TestData.Ref("s1"), Image, [], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Unsupported, assets.Error?.Code);
        Assert.Equal(0, this.gateway.ResolveCalls);
    }

    /// <summary>A node the host has never seen is passed to the plugin, which decides.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ResolveLetsThePluginDecideForUnknownNodes()
    {
        this.ResolvesTo(Remote("img.example.com"));

        await this.pipeline.ResolveAsync(TestData.Ref("never-seen"), AssetRole.Create("anything"), [], CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(1, this.gateway.ResolveCalls);
    }

    /// <summary>Inline assets are returned directly with the asset's mime type.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task InlinePayloadIsReturnedDirectly()
    {
        Asset asset = new("text/plain", new InlineDelivery(new byte[] { 9 }), null, []);

        Outcome<AssetPayload> payload = await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, asset, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("text/plain", payload.Value.Mime);
        Assert.Equal([9], payload.Value.Payload.ToArray());
        Assert.Equal(0, this.fetcher.Calls);
    }

    /// <summary>A remote asset is fetched once and then served from the cache, under the asset's own mime type.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RemoteAssetIsFetchedOnceThenCached()
    {
        Asset asset = Remote("img.example.com") with { Mime = "image/webp" };

        Outcome<AssetPayload> first = await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, asset, CancellationToken.None).ConfigureAwait(true);
        Outcome<AssetPayload> second = await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, asset, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal("image/webp", first.Value.Mime);
        Assert.Equal(first.Value.Payload.ToArray(), second.Value.Payload.ToArray());
        Assert.Equal(1, this.fetcher.Calls);
    }

    /// <summary>Different URLs for the same node and role are cached separately.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DifferentUrlsAreCachedSeparately()
    {
        await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, Remote("img.example.com", "/a.png"), CancellationToken.None).ConfigureAwait(true);
        await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, Remote("img.example.com", "/b.png"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, this.fetcher.Calls);
    }

    /// <summary>An expired link is not fetched; the caller is told to resolve again.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ExpiredLinkIsNotFetched()
    {
        Asset asset = new("image/png", new RemoteDelivery(TestData.Url("img.example.com"), [], this.time.GetUtcNow().AddMinutes(-1)), null, []);

        Outcome<AssetPayload> payload = await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, asset, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.NotFound, payload.Error?.Code);
        Assert.Equal(0, this.fetcher.Calls);
    }

    /// <summary>A failed fetch is returned as is and is not cached.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task FailedFetchIsNotCached()
    {
        this.fetcher.OnFetch = _ => Outcome.Failure<AssetPayload>(ErrorCode.NotFound, "gone");
        Asset asset = Remote("img.example.com");

        Outcome<AssetPayload> first = await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, asset, CancellationToken.None).ConfigureAwait(true);
        await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, asset, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.NotFound, first.Error?.Code);
        Assert.Equal(2, this.fetcher.Calls);
    }

    /// <summary>A proxied asset is read through the plugin; an oversized result is rejected.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ProxiedAssetIsReadThroughThePlugin()
    {
        Asset asset = new("image/png", new ProxiedDelivery("tok"), null, []);
        this.gateway.OnRead = (_, token, _) => Outcome.Success(new Chunk(new byte[] { 1, 2 }, 2));

        Outcome<AssetPayload> small = await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, asset, CancellationToken.None).ConfigureAwait(true);
        this.gateway.OnRead = (_, _, _) => Outcome.Success(new Chunk(new byte[2048], 2048));
        Outcome<AssetPayload> big = await this.pipeline.GetPayloadAsync(TestData.Ref("p1"), Image, asset, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(2, small.Value.Payload.Length);
        Assert.Equal(ErrorCode.Internal, big.Error?.Code);
    }

    /// <summary>Range reads validate the range and reject a plugin that returns more than asked.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task RangeReadsAreValidated()
    {
        ProxiedDelivery asset = new("tok");
        this.gateway.OnRead = (_, _, _) => Outcome.Success(new Chunk(new byte[10], 100));

        Outcome<Chunk> negative = await this.pipeline.ReadRangeAsync(TestData.Ref("p1"), asset, new ByteRange(-1, 5), CancellationToken.None).ConfigureAwait(true);
        Outcome<Chunk> empty = await this.pipeline.ReadRangeAsync(TestData.Ref("p1"), asset, new ByteRange(0, 0), CancellationToken.None).ConfigureAwait(true);
        Outcome<Chunk> tooMuch = await this.pipeline.ReadRangeAsync(TestData.Ref("p1"), asset, new ByteRange(0, 5), CancellationToken.None).ConfigureAwait(true);
        Outcome<Chunk> fine = await this.pipeline.ReadRangeAsync(TestData.Ref("p1"), asset, new ByteRange(0, 10), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ErrorCode.InvalidArgument, negative.Error?.Code);
        Assert.Equal(ErrorCode.InvalidArgument, empty.Error?.Code);
        Assert.Equal(ErrorCode.Internal, tooMuch.Error?.Code);
        Assert.True(fine.IsSuccess);
        Assert.Equal(2, this.gateway.ReadCalls);
    }

    private static Asset Remote(string host, string path = "/a.png") =>
        new("image/png", new RemoteDelivery(TestData.Url(host, path), [], null), null, []);

    private void ResolvesTo(params Asset[] assets) =>
        this.gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(assets);
}
