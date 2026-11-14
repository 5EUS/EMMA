// The fixtures name the addresses of the media they pretend to serve.
#pragma warning disable S1075
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using EMMA.Domain;
using EMMA.TestSupport;

using Xunit;

namespace EMMA.Api.Tests;

/// <summary>Tests for the playback plan, opening a stream, and serving it to a player.</summary>
public sealed class PlaybackEndpointTests
{
    private const string Episode = "ref=com.example.test:ep1";

    /// <summary>A plan lists what a node offers, typed, and never says where any of it lives.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task APlanListsWhatANodeOffersWithoutSayingWhereItLives() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);
        server.Gateway.OnResolve = (_, request) => Outcome.Success<IReadOnlyList<Asset>>(request.Role.Value switch
        {
            "stream" => [Remote("application/vnd.apple.mpegurl", "/secret/master.m3u8", new Prop("emma.quality", PropValue.FromText("1080p")), new Prop("emma.width", PropValue.FromWholeNumber(1920)))],
            "subtitle" => [Remote("text/vtt", "/secret/en.vtt", new Prop("emma.language", PropValue.FromText("en")))],
            _ => [],
        });

        ApiResponse response = await server.GetAsync($"/v1/playback?{Episode}&hint=1080p").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("com.example.test:ep1", response.Json["ref"]!.GetValue<string>());
        Assert.Equal("stream", response.Json["primary"]!.GetValue<string>());
        Assert.Equal("1080p", response.Json["sources"]![0]!["quality"]!.GetValue<string>());
        Assert.Equal(1920, response.Json["sources"]![0]!["width"]!.GetValue<int>());
        Assert.Equal("en", response.Json["subtitles"]![0]!["language"]!.GetValue<string>());
        Assert.Equal("subtitle", response.Json["subtitles"]![0]!["role"]!.GetValue<string>());
        Assert.DoesNotContain("secret", response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com/", response.Text.Replace("com.example.test", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    });

    /// <summary>A node the host never saw, or one with nothing to play, or a bad request, gets a clear refusal.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task APlanRefusesWhatCannotBePlayed() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);
        await server.RememberAsync(new Node(TestData.Ref("pic"), NodeKind.Create("picture"), "Pic", null, null, [])).ConfigureAwait(true);

        ApiResponse unseen = await server.GetAsync("/v1/playback?ref=com.example.test:nope").ConfigureAwait(true);
        ApiResponse nothing = await server.GetAsync("/v1/playback?ref=com.example.test:pic").ConfigureAwait(true);
        ApiResponse noRef = await server.GetAsync("/v1/playback").ConfigureAwait(true);
        ApiResponse badHint = await server.GetAsync($"/v1/playback?{Episode}&hint=").ConfigureAwait(true);
        ApiResponse anonymous = await server.GetAsync($"/v1/playback?{Episode}", key: null).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, unseen.Status);
        Assert.Equal(HttpStatusCode.NotImplemented, nothing.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noRef.Status);
        Assert.Equal(HttpStatusCode.BadRequest, badHint.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
    });

    /// <summary>An opened stream is a path on the API: the playlist comes back rewritten, and each part it names is served with the plugin's headers.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task AnOpenedPlaylistIsServedAndItsSegmentsFollow() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(
            [new Asset("application/vnd.apple.mpegurl", new RemoteDelivery(TestData.Url("api.example.com", "/v/index.m3u8"), [new HttpHeader("Referer", "https://example.com/")], null), null, [])]);
        server.Fetcher.OnFetch = delivery => Outcome.Success(new AssetPayload(
            "application/octet-stream",
            delivery.Url.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:4,\nseg1.ts\n#EXT-X-ENDLIST\n") : new byte[] { 1, 2, 3, 4, 5, 6 },
            DateTimeOffset.UnixEpoch));

        ApiResponse opened = await server.SendAsync(HttpMethod.Post, $"/v1/playback/open?{Episode}&role=stream").ConfigureAwait(true);
        string path = opened.Json["url"]!.GetValue<string>();
        ApiResponse playlist = await server.GetAsync(path).ConfigureAwait(true);
        string segmentPath = path[..path.LastIndexOf('/')] + "/" + playlist.Text.Split('\n').Single(line => line.Length > 0 && !line.StartsWith('#'));
        ApiResponse segment = await server.GetAsync(segmentPath).ConfigureAwait(true);
        ApiResponse partial = await server.SendAsync(HttpMethod.Get, segmentPath, configure: request => request.Headers.Range = new RangeHeaderValue(2, 3)).ConfigureAwait(true);
        ApiResponse beyond = await server.SendAsync(HttpMethod.Get, segmentPath, configure: request => request.Headers.Range = new RangeHeaderValue(50, null)).ConfigureAwait(true);
        ApiResponse head = await server.SendAsync(HttpMethod.Head, segmentPath).ConfigureAwait(true);
        ApiResponse anonymous = await server.GetAsync(path, key: null).ConfigureAwait(true);

        Assert.Equal("application/vnd.apple.mpegurl", opened.Json["mime"]!.GetValue<string>());
        Assert.StartsWith("/v1/stream/", path, StringComparison.Ordinal);
        Assert.EndsWith("/0", path, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, playlist.Status);
        Assert.Equal("application/vnd.apple.mpegurl", playlist.Header("Content-Type"));
        Assert.DoesNotContain("example.com", playlist.Text, StringComparison.Ordinal);
        Assert.Equal("no-store", playlist.Header("Cache-Control"));
        Assert.Equal("nosniff", playlist.Header("X-Content-Type-Options"));
        Assert.Equal([1, 2, 3, 4, 5, 6], segment.Body);
        Assert.Equal(HttpStatusCode.PartialContent, partial.Status);
        Assert.Equal([3, 4], partial.Body);
        Assert.Equal("bytes 2-3/6", partial.Header("Content-Range"));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, beyond.Status);
        Assert.Equal("bytes */6", beyond.Header("Content-Range"));
        Assert.Equal(HttpStatusCode.OK, head.Status);
        Assert.Empty(head.Body);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        Assert.All(server.Fetcher.Opens, open => Assert.Equal(new HttpHeader("Referer", "https://example.com/"), open.Delivery.Headers.Single()));
    });

    /// <summary>A path beneath a DASH base reaches the route, with its query, and fetches from the manifest's directory.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task MediaBeneathADashBaseIsServedWithItsQuery() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);
        server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>(
            [new Asset("application/dash+xml", new RemoteDelivery(TestData.Url("api.example.com", "/d/manifest.mpd"), [], null), null, [])]);
        server.Fetcher.OnFetch = delivery => Outcome.Success(new AssetPayload(
            "application/octet-stream",
            delivery.Url.AbsolutePath.EndsWith(".mpd", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("<MPD><Period/></MPD>") : new byte[] { 9 },
            DateTimeOffset.UnixEpoch));
        string path = (await server.SendAsync(HttpMethod.Post, $"/v1/playback/open?{Episode}&role=stream").ConfigureAwait(true)).Json["url"]!.GetValue<string>();
        ApiResponse manifest = await server.GetAsync(path).ConfigureAwait(true);

        ApiResponse media = await server.GetAsync(path[..^1] + "1/video/seg-1.m4s?sig=abc").ConfigureAwait(true);
        ApiResponse escape = await server.GetAsync(path[..^1] + "1/%2e%2e/secret").ConfigureAwait(true);

        Assert.Contains("<BaseURL>1/</BaseURL>", manifest.Text, StringComparison.Ordinal);
        Assert.Equal([9], media.Body);
        Assert.Equal("https://api.example.com/d/video/seg-1.m4s?sig=abc", server.Fetcher.Opens.Single().Delivery.Url.AbsoluteUri);
        Assert.NotEqual(HttpStatusCode.OK, escape.Status);
    });

    /// <summary>Serving stream parts is not counted against the per-client allowance, which a player would use up in seconds; the rest of the API still is.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task StreamPartsAreNotRateLimited() => ApiTests.WithServerAsync(
        async server =>
        {
            await SeedAsync(server).ConfigureAwait(true);
            server.Gateway.OnResolve = (_, _) => Outcome.Success<IReadOnlyList<Asset>>([new Asset("audio/mpeg", new InlineDelivery(new byte[] { 1 }), 1, [])]);
            ApiResponse opened = await server.SendAsync(HttpMethod.Post, $"/v1/playback/open?{Episode}&role=audio").ConfigureAwait(true);
            string path = opened.Json["url"]!.GetValue<string>();

            List<HttpStatusCode> served = [];
            for (int request = 0; request < 12; request++)
            {
                served.Add((await server.GetAsync(path).ConfigureAwait(true)).Status);
            }

            ApiResponse limited = await server.GetAsync("/v1/plugins").ConfigureAwait(true);

            Assert.All(served, status => Assert.Equal(HttpStatusCode.OK, status));
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        },
        perClientPermits: 1);

    /// <summary>Opening checks its parameters, and a stream nobody opened is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task OpeningAndServingRefuseWhatIsWrong() => ApiTests.WithServerAsync(async server =>
    {
        await SeedAsync(server).ConfigureAwait(true);

        ApiResponse noRole = await server.SendAsync(HttpMethod.Post, $"/v1/playback/open?{Episode}").ConfigureAwait(true);
        ApiResponse badRole = await server.SendAsync(HttpMethod.Post, $"/v1/playback/open?{Episode}&role=Not%20A%20Role").ConfigureAwait(true);
        ApiResponse badIndex = await server.SendAsync(HttpMethod.Post, $"/v1/playback/open?{Episode}&role=stream&index=5000").ConfigureAwait(true);
        ApiResponse wrongMethod = await server.GetAsync($"/v1/playback/open?{Episode}&role=stream").ConfigureAwait(true);
        ApiResponse unknown = await server.GetAsync("/v1/stream/nobody/0").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, noRole.Status);
        Assert.Equal(HttpStatusCode.BadRequest, badRole.Status);
        Assert.Equal(HttpStatusCode.BadRequest, badIndex.Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.Status);
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        Assert.Equal("not_found", unknown.ErrorCode);
    });

    private static Asset Remote(string mime, string path, params Prop[] props) =>
        new(mime, new RemoteDelivery(TestData.Url("api.example.com", path), [], null), null, props);

    private static Task SeedAsync(ApiTestServer server)
    {
        server.Gateway.OnDescribe = _ => Outcome.Success(new PluginInfo(
            [
                TestData.Kind("episode", roles: ["stream", "subtitle", "audio"]),
                TestData.Kind("picture", roles: ["image"]),
            ],
            [],
            [],
            [],
            []));
        return server.RememberAsync(new Node(TestData.Ref("ep1"), NodeKind.Create("episode"), "Episode 1", null, null, []));
    }
}
