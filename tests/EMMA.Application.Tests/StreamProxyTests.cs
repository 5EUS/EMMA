// The fixtures name the addresses of the streams they pretend to serve.
#pragma warning disable S1075
using System.Text;

using EMMA.Application.Pipelines;
using EMMA.Application.Streaming;
using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.InMemory;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="StreamProxy"/>: opening an asset for a player, and answering what the player asks for.</summary>
public sealed class StreamProxyTests : IAsyncDisposable
{
    private static readonly AssetRole Stream = AssetRole.Create("stream");
    private static readonly NodeRef Episode = TestData.Ref("ep1");

    private readonly DownloadPipeline downloads;

    private readonly FakeTimeProvider time = new();
    private readonly FakePluginGateway gateway = new();
    private readonly FakeAssetFetcher fetcher;
    private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);
    private readonly StreamProxy proxy;
    private readonly AssetPipeline assets;
    private readonly FakePolicies policies;
    private readonly BoundedAssetCache cache;
    private readonly PluginCallExecutor executor;
    private bool offline;

    /// <summary>Initializes a new instance of the <see cref="StreamProxyTests"/> class.</summary>
    public StreamProxyTests()
    {
        this.fetcher = new FakeAssetFetcher(this.time)
        {
            OnFetch = this.Serve,
        };
        this.executor = new PluginCallExecutor(new PluginCallOptions(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(30)), this.time);
        InMemoryNodeStore nodes = new();
        this.policies = new FakePolicies().Allow(TestData.Plugin, "cdn.example.com", "seg.example.com");
        this.cache = new BoundedAssetCache(new AssetCacheOptions(8 * 1024 * 1024, 0, "unused", TimeSpan.FromDays(1)), this.time);
        this.assets = new AssetPipeline(this.gateway, this.policies, nodes, this.fetcher, this.cache, new BoundedResolutionCache(64, this.time), this.executor, new AssetPipelineOptions(8 * 1024 * 1024), this.time);
        CatalogPipeline catalog = new(this.gateway, nodes, new InMemoryNodeListingStore(), this.executor);
        this.downloads = new DownloadPipeline(new FakeDownloadStore(), new FakeDownloadFiles(), catalog, this.assets, nodes, new FakeAppSettings(), this.time);
        this.proxy = this.Proxy(new StreamProxyOptions { MaxSessions = 2, ProxiedChunkBytes = 1000 });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A playlist is rewritten so every playlist and segment comes back through the proxy, with the plugin's headers.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task APlaylistAndItsSegmentsAreServedThroughTheProxy()
    {
        this.ServesRemote("https://cdn.example.com/v/master.m3u8", "application/vnd.apple.mpegurl", [new HttpHeader("Referer", "https://example.com/")]);
        this.content["https://cdn.example.com/v/master.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nlow/index.m3u8\n");
        this.content["https://cdn.example.com/v/low/index.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:4,\nseg1.ts\n#EXT-X-ENDLIST\n");
        this.content["https://cdn.example.com/v/low/seg1.ts"] = [1, 2, 3, 4, 5];

        StreamLink link = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        string master = await this.TextAsync($"{link.Path}").ConfigureAwait(true);
        string variantPart = master.Split('\n').Single(line => !line.StartsWith('#') && line.Length > 0);
        string variant = await this.TextAsync($"{link.Session}/{variantPart}").ConfigureAwait(true);
        string segmentPart = variant.Split('\n').Single(line => !line.StartsWith('#') && line.Length > 0);
        byte[] segment = await this.BytesAsync($"{link.Session}/{segmentPart}").ConfigureAwait(true);

        Assert.Equal("application/vnd.apple.mpegurl", link.Mime);
        Assert.DoesNotContain("example.com", master + variant, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4, 5], segment);
        Assert.All(this.fetcher.Opens, open => Assert.Equal(new HttpHeader("Referer", "https://example.com/"), open.Delivery.Headers.Single()));
        Assert.Equal("https://cdn.example.com/v/low/seg1.ts", this.fetcher.Opens.Single().Delivery.Url.AbsoluteUri);
    }

    /// <summary>A segment is fetched once and then served from the cache, including for a range.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ASegmentIsFetchedOnceAndServedFromTheCache()
    {
        string path = await this.OpenMediaPlaylistAsync().ConfigureAwait(true);

        byte[] first = await this.BytesAsync(path).ConfigureAwait(true);
        byte[] second = await this.BytesAsync(path).ConfigureAwait(true);
        ServedPart part = await this.ServeAsync(path, new ByteRange(1, 2)).ConfigureAwait(true);

        Assert.Equal([1, 2, 3, 4, 5], first);
        Assert.Equal(first, second);
        Assert.Single(this.fetcher.Opens);
        Assert.Equal(1, part.RangeStart);
        Assert.Equal(5, part.Total);
        Assert.Equal([2, 3], part.Bytes);
    }

    /// <summary>
    /// A player asks for every segment with a range (ffmpeg sends <c>bytes=0-</c>), and that must not keep the segment from being cached;
    /// a part keeps the file extension of what it leads to, which a strict HLS client insists on.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ARangedFirstRequestStillCachesTheSegmentAndPartsKeepTheirExtension()
    {
        string path = await this.OpenMediaPlaylistAsync().ConfigureAwait(true);

        ServedPart first = await this.ServeAsync(path, new ByteRange(0, null)).ConfigureAwait(true);
        ServedPart second = await this.ServeAsync(path, new ByteRange(2, null)).ConfigureAwait(true);

        Assert.EndsWith(".ts", path, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3, 4, 5], first.Bytes);
        Assert.Equal([3, 4, 5], second.Bytes);
        Assert.Equal(2, second.RangeStart);
        Assert.Equal(5, second.Total);
        Assert.Single(this.fetcher.Opens);
        Assert.Null(this.fetcher.Opens.Single().Range);
    }

    /// <summary>A segment too big to keep is passed through with the range the player asked for.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ASegmentTooBigToKeepIsPassedThrough()
    {
        StreamProxy small = this.Proxy(new StreamProxyOptions { MaxSegmentBytes = 3 });
        string path = await this.OpenMediaPlaylistAsync(small).ConfigureAwait(true);

        ServedPart response = await this.ServeAsync(path, new ByteRange(1, 2), through: small).ConfigureAwait(true);

        Assert.Equal(1, response.RangeStart);
        Assert.Equal([2, 3], response.Bytes);
        Assert.Equal(2, this.fetcher.Opens.Count);
        Assert.Equal(new ByteRange(1, 2), this.fetcher.Opens[1].Range);
        Assert.Null(this.fetcher.Opens[0].Range);
    }

    /// <summary>Credentials stay with the host they were for: a segment on another host gets the other headers but no cookie.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task CredentialsDoNotFollowAPlaylistToAnotherHost()
    {
        this.ServesRemote("https://cdn.example.com/v/index.m3u8", "application/x-mpegURL", [new HttpHeader("Cookie", "s=1"), new HttpHeader("Referer", "https://example.com/")]);
        this.content["https://cdn.example.com/v/index.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:4,\nhttps://seg.example.com/a.ts\n#EXTINF:4,\nb.ts\n");
        this.content["https://seg.example.com/a.ts"] = [1];
        this.content["https://cdn.example.com/v/b.ts"] = [2];
        StreamLink link = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        string[] parts = [.. (await this.TextAsync(link.Path).ConfigureAwait(true)).Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#'))];

        await this.BytesAsync($"{link.Session}/{parts[0]}").ConfigureAwait(true);
        await this.BytesAsync($"{link.Session}/{parts[1]}").ConfigureAwait(true);

        Assert.Equal(["Referer"], this.fetcher.Opens[0].Delivery.Headers.Select(header => header.Name));
        Assert.Equal(["Cookie", "Referer"], this.fetcher.Opens[1].Delivery.Headers.Select(header => header.Name));
    }

    /// <summary>When upstream cannot be reached, the last playlist is served cut down to the segments the cache still holds.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnUnreachablePlaylistIsRepairedFromTheCache()
    {
        this.ServesRemote("https://cdn.example.com/v/index.m3u8", "application/vnd.apple.mpegurl", []);
        this.content["https://cdn.example.com/v/index.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns1.ts\n#EXTINF:4,\ns2.ts\n#EXTINF:4,\ns3.ts\n");
        this.content["https://cdn.example.com/v/s1.ts"] = [1];
        this.content["https://cdn.example.com/v/s3.ts"] = [3];
        StreamLink link = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        string[] live = [.. (await this.TextAsync(link.Path).ConfigureAwait(true)).Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#'))];
        await this.BytesAsync($"{link.Session}/{live[0]}").ConfigureAwait(true);
        await this.BytesAsync($"{link.Session}/{live[2]}").ConfigureAwait(true);

        this.offline = true;
        string repaired = await this.TextAsync(link.Path).ConfigureAwait(true);

        string[] kept = [.. repaired.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#'))];
        Assert.Equal([live[0], live[2]], kept);
        Assert.Contains("#EXT-X-DISCONTINUITY", repaired, StringComparison.Ordinal);
        Assert.EndsWith("#EXT-X-ENDLIST\n", repaired, StringComparison.Ordinal);
    }

    /// <summary>With nothing cached, an unreachable playlist is an error, not an empty one.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnUnreachablePlaylistWithNothingCachedFails()
    {
        this.ServesRemote("https://cdn.example.com/v/index.m3u8", "application/vnd.apple.mpegurl", []);
        StreamLink link = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        this.offline = true;

        Outcome<StreamResponse> served = await this.proxy.ServeAsync(new StreamRequest(link.Path, null), Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Unavailable, served.Error?.Code);
    }

    /// <summary>A direct file is passed through with its range and never cached.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ADirectFileIsPassedThroughWithRangesAndNotCached()
    {
        this.ServesRemote("https://cdn.example.com/v/video.mp4", "video/mp4", []);
        this.content["https://cdn.example.com/v/video.mp4"] = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];
        StreamLink link = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;

        ServedPart range = await this.ServeAsync(link.Path, new ByteRange(10, 5)).ConfigureAwait(true);
        ServedPart past = await this.ServeAsync(link.Path, new ByteRange(500, null)).ConfigureAwait(true);
        ServedPart whole = await this.ServeAsync(link.Path, null).ConfigureAwait(true);

        Assert.Equal([10, 11, 12, 13, 14], range.Bytes);
        Assert.Equal(10, range.RangeStart);
        Assert.Equal(100, range.Total);
        Assert.Equal("video/mp4", range.Mime);
        Assert.True(past.NotSatisfiable);
        Assert.Null(whole.RangeStart);
        Assert.Equal(100, whole.Bytes.Length);
        Assert.Equal(3, this.fetcher.Opens.Count);
    }

    /// <summary>An asset only the plugin can read is streamed in pieces, whole or by range, without holding it all.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnAssetOnlyThePluginCanReadIsStreamedInPieces()
    {
        byte[] data = [.. Enumerable.Range(0, 3500).Select(i => (byte)(i % 251))];
        List<ByteRange?> reads = [];
        this.gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("audio/mpeg", new ProxiedDelivery("tok"), data.Length, [])]);
        this.gateway.OnRead = (_, _, range) =>
        {
            reads.Add(range);
            int start = (int)Math.Min(range?.Start ?? 0, data.Length);
            int length = (int)Math.Min(range?.Length ?? data.Length, data.Length - start);
            return Outcome.Success(new Chunk(data.AsMemory(start, length), data.Length));
        };
        StreamLink link = (await this.proxy.OpenAsync(Episode, AssetRole.Create("audio"), 0, null, Ct).ConfigureAwait(true)).Value;

        ServedPart whole = await this.ServeAsync(link.Path, null).ConfigureAwait(true);
        ServedPart range = await this.ServeAsync(link.Path, new ByteRange(1500, 2000)).ConfigureAwait(true);
        ServedPart tail = await this.ServeAsync(link.Path, new ByteRange(3000, null)).ConfigureAwait(true);
        ServedPart past = await this.ServeAsync(link.Path, new ByteRange(9000, null)).ConfigureAwait(true);

        Assert.Equal(data, whole.Bytes);
        Assert.Equal(data.Length, whole.Length);
        Assert.Equal(data[1500..3500], range.Bytes);
        Assert.Equal(2000, range.Length);
        Assert.Equal(data[3000..], tail.Bytes);
        Assert.True(past.NotSatisfiable);
        Assert.All(reads, read => Assert.True(read?.Length <= 1000));
    }

    /// <summary>Inline bytes are served, with ranges, and a stream that is not a playlist or file needs nothing fetched.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task InlineBytesAreServedWithRanges()
    {
        this.gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("audio/ogg", new InlineDelivery(new byte[] { 9, 8, 7, 6 }), 4, [])]);
        StreamLink link = (await this.proxy.OpenAsync(Episode, AssetRole.Create("audio"), 0, null, Ct).ConfigureAwait(true)).Value;

        ServedPart range = await this.ServeAsync(link.Path, new ByteRange(1, 2)).ConfigureAwait(true);

        Assert.Equal([8, 7], range.Bytes);
        Assert.Empty(this.fetcher.Opens);
    }

    /// <summary>A DASH manifest gets a base the proxy serves; media beneath it is fetched from the manifest's directory, query included.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ADashManifestIsServedWithABaseTheProxyAnswers()
    {
        this.ServesRemote("https://cdn.example.com/d/manifest.mpd", "application/dash+xml", [new HttpHeader("Referer", "https://example.com/")]);
        this.content["https://cdn.example.com/d/manifest.mpd"] = Encoding.UTF8.GetBytes("<MPD><Period/></MPD>");
        this.content["https://cdn.example.com/d/video/seg-1.m4s?sig=abc"] = [4, 2];
        StreamLink link = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;

        string manifest = await this.TextAsync(link.Path).ConfigureAwait(true);
        byte[] segment = await this.BytesAsync($"{link.Session}/1/video/seg-1.m4s", "sig=abc").ConfigureAwait(true);

        Assert.Contains("<BaseURL>1/</BaseURL>", manifest, StringComparison.Ordinal);
        Assert.Equal([4, 2], segment);
        Assert.Equal("https://cdn.example.com/d/video/seg-1.m4s?sig=abc", this.fetcher.Opens.Single().Delivery.Url.AbsoluteUri);
    }

    /// <summary>A path that climbs out of a directory part, or goes beneath a part that is not one, is not found.</summary>
    /// <param name="path">The part of the path after the session.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("1/../secret")]
    [InlineData("1/a\\b")]
    [InlineData("1")]
    [InlineData("0/extra")]
    [InlineData("99")]
    [InlineData("x")]
    public async Task PathsThatLeadNowhereAreNotFound(string path)
    {
        this.ServesRemote("https://cdn.example.com/d/manifest.mpd", "application/dash+xml", []);
        this.content["https://cdn.example.com/d/manifest.mpd"] = Encoding.UTF8.GetBytes("<MPD/>");
        StreamLink link = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        await this.TextAsync(link.Path).ConfigureAwait(true);

        Outcome<StreamResponse> served = await this.proxy.ServeAsync(new StreamRequest($"{link.Session}/{path}", null), Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.NotFound, served.Error?.Code);
    }

    /// <summary>A session nobody knows, or one unused for too long, or one pushed out by newer ones, is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task SessionsExpireAndAreEvicted()
    {
        this.ServesRemote("https://cdn.example.com/v/video.mp4", "video/mp4", []);
        this.content["https://cdn.example.com/v/video.mp4"] = [1];
        StreamLink first = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        this.time.Advance(TimeSpan.FromMinutes(1));
        await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true);
        await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true);

        Outcome<StreamResponse> evicted = await this.proxy.ServeAsync(new StreamRequest(first.Path, null), Ct).ConfigureAwait(true);
        Outcome<StreamResponse> unknown = await this.proxy.ServeAsync(new StreamRequest("nope/0", null), Ct).ConfigureAwait(true);
        StreamLink fresh = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        this.time.Advance(TimeSpan.FromHours(7));
        Outcome<StreamResponse> expired = await this.proxy.ServeAsync(new StreamRequest(fresh.Path, null), Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.NotFound, evicted.Error?.Code);
        Assert.Equal(ErrorCode.NotFound, unknown.Error?.Code);
        Assert.Equal(ErrorCode.NotFound, expired.Error?.Code);
    }

    /// <summary>A link that died before its stated expiry is replaced by asking the plugin again, once, and only for the first part.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ADeadLinkIsResolvedAgain()
    {
        int resolves = 0;
        this.gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(
        [
            new Asset("video/mp4", new RemoteDelivery(new Uri($"https://cdn.example.com/v/{(resolves++ == 0 ? "old" : "new")}.mp4"), [], null), null, []),
        ]);
        this.content["https://cdn.example.com/v/new.mp4"] = [7, 7];
        StreamLink link = (await this.proxy.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;

        byte[] served = await this.BytesAsync(link.Path).ConfigureAwait(true);

        Assert.Equal([7, 7], served);
        Assert.Equal(2, this.gateway.ResolveCalls);
    }

    /// <summary>Opening refuses an index the node does not have, and passes on why resolving failed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task OpeningRefusesWhatCannotBePlayed()
    {
        this.ServesRemote("https://cdn.example.com/v/video.mp4", "video/mp4", []);

        Outcome<StreamLink> missing = await this.proxy.OpenAsync(Episode, Stream, 3, null, Ct).ConfigureAwait(true);
        this.gateway.OnResolve = (_, _) => Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.Unauthorized, "log in");
        Outcome<StreamLink> refused = await this.proxy.OpenAsync(TestData.Ref("ep2"), Stream, 0, null, Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.NotFound, missing.Error?.Code);
        Assert.Equal(ErrorCode.Unauthorized, refused.Error?.Code);
    }

    /// <summary>The quality hint reaches the plugin.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheHintReachesThePlugin()
    {
        IReadOnlyList<Prop>? seen = null;
        this.gateway.OnResolve = (_, request) =>
        {
            seen = request.Hints;
            return Outcome.Success<IReadOnlyList<Asset>>([new Asset("video/mp4", new InlineDelivery(new byte[] { 1 }), null, [])]);
        };

        await this.proxy.OpenAsync(Episode, Stream, 0, "720p", Ct).ConfigureAwait(true);

        Assert.Equal(PropValue.FromText("720p"), seen!.Single().Value);
    }

    /// <summary>Releases the download pipeline the proxy was built with.</summary>
    /// <returns>A task that completes when it is released.</returns>
    public ValueTask DisposeAsync() => this.downloads.DisposeAsync();

    private StreamProxy Proxy(StreamProxyOptions options) =>
        new(this.assets, this.policies, this.fetcher, this.cache, this.downloads, this.executor, options, this.time);

    private Outcome<AssetPayload> Serve(RemoteDelivery delivery)
    {
        if (this.offline)
        {
            return Outcome.Failure<AssetPayload>(ErrorCode.Unavailable, "offline");
        }

        return this.content.TryGetValue(delivery.Url.AbsoluteUri, out byte[]? bytes)
            ? Outcome.Success(new AssetPayload("application/octet-stream", bytes, this.time.GetUtcNow()))
            : Outcome.Failure<AssetPayload>(ErrorCode.NotFound, "no such thing");
    }

    private void ServesRemote(string url, string mime, IReadOnlyList<HttpHeader> headers) =>
        this.gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset(mime, new RemoteDelivery(new Uri(url), headers, null), null, [])]);

    /// <summary>Opens a one-segment media playlist and returns the path of its segment.</summary>
    private async Task<string> OpenMediaPlaylistAsync(StreamProxy? through = null)
    {
        StreamProxy used = through ?? this.proxy;
        this.ServesRemote("https://cdn.example.com/v/index.m3u8", "application/vnd.apple.mpegurl", []);
        this.content["https://cdn.example.com/v/index.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:4,\nseg1.ts\n#EXT-X-ENDLIST\n");
        this.content["https://cdn.example.com/v/seg1.ts"] = [1, 2, 3, 4, 5];
        StreamLink link = (await used.OpenAsync(Episode, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        string playlist = await this.TextAsync(link.Path, used).ConfigureAwait(true);
        return $"{link.Session}/{playlist.Split('\n').Single(line => line.Length > 0 && !line.StartsWith('#'))}";
    }

    private async Task<ServedPart> ServeAsync(string path, ByteRange? range, string? query = null, StreamProxy? through = null)
    {
        Outcome<StreamResponse> served = await (through ?? this.proxy).ServeAsync(new StreamRequest(path, range, query), Ct).ConfigureAwait(true);
        Assert.True(served.IsSuccess, served.Error?.Message);
        StreamResponse response = served.Value;
        await using (response.ConfigureAwait(true))
        {
            using MemoryStream copy = new();
            await response.Body.CopyToAsync(copy, Ct).ConfigureAwait(true);
            return new ServedPart(response.Mime, copy.ToArray(), response.Length, response.RangeStart, response.Total, response.RangeNotSatisfiable);
        }
    }

    private async Task<byte[]> BytesAsync(string path, string? query = null, StreamProxy? through = null) =>
        (await this.ServeAsync(path, null, query, through).ConfigureAwait(true)).Bytes;

    private async Task<string> TextAsync(string path, StreamProxy? through = null) =>
        Encoding.UTF8.GetString(await this.BytesAsync(path, null, through).ConfigureAwait(true));

    private sealed record ServedPart(string Mime, byte[] Bytes, long? Length, long? RangeStart, long? Total, bool NotSatisfiable);
}
