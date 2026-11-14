// The fixtures name the addresses of the media they pretend to serve.
#pragma warning disable S1075
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Runtime.Tests;

/// <summary>Tests the local server an app's player is given an address on, over a real socket.</summary>
public sealed class PlaybackTests
{
    private static readonly NodeRef Track = TestData.Ref("t1");
    private static readonly AssetRole Audio = AssetRole.Create("audio");
    private static readonly AssetRole Stream = AssetRole.Create("stream");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A plugin-served file is playable from an address on this machine, whole or by range, and HEAD says how big it is.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task APluginServedFileIsPlayableFromALocalAddress() => WithRuntimeAsync(async (runtime, gateway, _, _) =>
    {
        byte[] data = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i % 251))];
        gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("audio/mpeg", new ProxiedDelivery("tok"), data.Length, [])]);
        gateway.OnRead = (_, _, range) =>
        {
            int start = (int)Math.Min(range?.Start ?? 0, data.Length);
            int length = (int)Math.Min(range?.Length ?? data.Length, data.Length - start);
            return Outcome.Success(new Chunk(data.AsMemory(start, length), data.Length));
        };
        OpenedPlayback opened = (await runtime.OpenStreamUrlAsync(Track, Audio, 0, null, Ct).ConfigureAwait(true)).Value;
        using HttpClient http = new();

        using HttpResponseMessage whole = await http.GetAsync(opened.Url, Ct).ConfigureAwait(true);
        using HttpRequestMessage rangeRequest = new(HttpMethod.Get, opened.Url) { Headers = { Range = new RangeHeaderValue(1000, 1999) } };
        using HttpResponseMessage range = await http.SendAsync(rangeRequest, Ct).ConfigureAwait(true);
        using HttpRequestMessage headRequest = new(HttpMethod.Head, opened.Url);
        using HttpResponseMessage head = await http.SendAsync(headRequest, Ct).ConfigureAwait(true);

        Assert.Equal("audio/mpeg", opened.Mime);
        Assert.Equal("127.0.0.1", opened.Url.Host);
        Assert.Equal(data, await whole.Content.ReadAsByteArrayAsync(Ct).ConfigureAwait(true));
        Assert.Equal("audio/mpeg", whole.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(data[1000..2000], await range.Content.ReadAsByteArrayAsync(Ct).ConfigureAwait(true));
        Assert.Equal(new ContentRangeHeaderValue(1000, 1999, 5000), range.Content.Headers.ContentRange);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(5000, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(Ct).ConfigureAwait(true));
        Assert.Contains("bytes", whole.Headers.AcceptRanges);
    });

    /// <summary>A playlist's relative addresses lead back to the same server, so a player that resolves them finds the segments.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task APlaylistsSegmentsAreFoundRelativeToIt() => WithRuntimeAsync(async (runtime, gateway, fetcher, _) =>
    {
        gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(
            [new Asset("application/vnd.apple.mpegurl", new RemoteDelivery(TestData.Url("cdn.example.com", "/v/index.m3u8"), [new HttpHeader("Referer", "https://example.com/")], null), null, [])]);
        fetcher.OnFetch = delivery => Outcome.Success(new AssetPayload(
            "application/octet-stream",
            delivery.Url.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:4,\nseg1.ts\n#EXT-X-ENDLIST\n") : new byte[] { 7, 7, 7 },
            DateTimeOffset.UnixEpoch));
        OpenedPlayback opened = (await runtime.OpenStreamUrlAsync(Track, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        using HttpClient http = new();

        string playlist = await http.GetStringAsync(opened.Url, Ct).ConfigureAwait(true);
        Uri segment = new(opened.Url, playlist.Split('\n').Single(line => line.Length > 0 && !line.StartsWith('#')));
        byte[] bytes = await http.GetByteArrayAsync(segment, Ct).ConfigureAwait(true);

        Assert.Equal([7, 7, 7], bytes);
        Assert.Equal(opened.Url.Authority, segment.Authority);
        Assert.DoesNotContain("example.com", playlist, StringComparison.Ordinal);
        Assert.Equal(new HttpHeader("Referer", "https://example.com/"), fetcher.Opens.Single().Delivery.Headers.Single());
    });

    /// <summary>Only a caller that knows the secret in the path, and addresses the server as itself, is answered; only GET and HEAD are.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task OnlyTheHolderOfTheAddressIsAnswered() => WithRuntimeAsync(async (runtime, gateway, _, _) =>
    {
        gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("audio/mpeg", new InlineDelivery(new byte[] { 1, 2 }), 2, [])]);
        OpenedPlayback opened = (await runtime.OpenStreamUrlAsync(Track, Audio, 0, null, Ct).ConfigureAwait(true)).Value;
        string[] segments = opened.Url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        using HttpClient http = new();

        using HttpResponseMessage noSecret = await http.GetAsync(new Uri(opened.Url, $"/{segments[1]}/0"), Ct).ConfigureAwait(true);
        using HttpResponseMessage wrongSecret = await http.GetAsync(new Uri(opened.Url, $"/{new string('0', 32)}/{segments[1]}/0"), Ct).ConfigureAwait(true);
        using HttpRequestMessage otherHost = new(HttpMethod.Get, opened.Url) { Headers = { Host = "rebind.example.com" } };
        using HttpResponseMessage rebinding = await http.SendAsync(otherHost, Ct).ConfigureAwait(true);
        using HttpRequestMessage post = new(HttpMethod.Post, opened.Url) { Content = new ByteArrayContent([1]) };
        using HttpResponseMessage posted = await http.SendAsync(post, Ct).ConfigureAwait(true);
        using HttpResponseMessage unknown = await http.GetAsync(new Uri(opened.Url, $"/{segments[0]}/nobody/0"), Ct).ConfigureAwait(true);
        using HttpResponseMessage fine = await http.GetAsync(opened.Url, Ct).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, noSecret.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rebinding.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal("nosniff", fine.Headers.GetValues("X-Content-Type-Options").Single());
    });

    /// <summary>An upstream that cannot be reached is a gateway error the player can act on, and nothing the client sent can stall the server.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ABrokenUpstreamAndGarbageDoNotStallTheServer() => WithRuntimeAsync(async (runtime, gateway, fetcher, _) =>
    {
        gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("video/mp4", new RemoteDelivery(TestData.Url("cdn.example.com", "/v.mp4"), [], null), null, [])]);
        fetcher.OnFetch = _ => Outcome.Failure<AssetPayload>(ErrorCode.Unavailable, "down");
        OpenedPlayback opened = (await runtime.OpenStreamUrlAsync(Track, Stream, 0, null, Ct).ConfigureAwait(true)).Value;
        using HttpClient http = new();

        using TcpClient garbage = new();
        await garbage.ConnectAsync(opened.Url.Host, opened.Url.Port, Ct).ConfigureAwait(true);
        NetworkStream stream = garbage.GetStream();
        await using (stream.ConfigureAwait(true))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("not http at all\r\n\r\n"), Ct).ConfigureAwait(true);
        }

        using HttpResponseMessage broken = await http.GetAsync(opened.Url, Ct).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadGateway, broken.StatusCode);
    });

    /// <summary>Opening refuses what the API would, and closing the runtime stops the server.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task OpeningRefusesWhatCannotBePlayedAndDisposingStopsTheServer()
    {
        Uri address = null!;
        await WithRuntimeAsync(async (runtime, gateway, _, _) =>
        {
            gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("audio/mpeg", new InlineDelivery(new byte[] { 1 }), 1, [])]);
            Outcome<OpenedPlayback> missing = await runtime.OpenStreamUrlAsync(Track, Audio, 4, null, Ct).ConfigureAwait(true);
            address = (await runtime.OpenStreamUrlAsync(Track, Audio, 0, null, Ct).ConfigureAwait(true)).Value.Url;
            Uri again = (await runtime.OpenStreamUrlAsync(Track, Audio, 0, null, Ct).ConfigureAwait(true)).Value.Url;

            Assert.Equal(ErrorCode.NotFound, missing.Error?.Code);
            Assert.Equal(address.Authority, again.Authority);
            Assert.NotEqual(address.AbsolutePath, again.AbsolutePath);
        }).ConfigureAwait(true);
        using HttpClient http = new();

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetByteArrayAsync(address, Ct)).ConfigureAwait(true);
    }

    private static async Task WithRuntimeAsync(Func<EmmaRuntime, FakePluginGateway, FakeAssetFetcher, RuntimeHarness, Task> body)
    {
        RuntimeHarness host = await RuntimeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            FakePluginGateway gateway = new();
            FakeAssetFetcher fetcher = new(TimeProvider.System);
            EmmaRuntime runtime = host.Open(options => options with
            {
                GatewayOverride = gateway,
                FetcherOverride = fetcher,
                CallOptions = new PluginCallOptions(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(30)),
            });
            await using (runtime.ConfigureAwait(true))
            {
                await runtime.InitializeAsync(Ct).ConfigureAwait(true);

                // The plugin must be installed for the host to let it point at cdn.example.com.
                PluginManifest manifest = TestManifests.Valid() with { Permissions = new ManifestPermissions(["cdn.example.com"], false) };
                await runtime.Registry.UpsertAsync(new InstalledPlugin(TestData.Plugin, "1.0.0", PluginStatus.Enabled, TestManifests.Json(manifest), null, TestPki.Now, TestPki.Now), Ct).ConfigureAwait(true);
                await runtime.Policies.RefreshAsync(runtime.Registry, Ct).ConfigureAwait(true);
                await body(runtime, gateway, fetcher, host).ConfigureAwait(true);
            }
        }
    }
}
