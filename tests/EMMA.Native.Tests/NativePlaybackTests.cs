using System.Net.Http.Headers;
using System.Text.Json.Nodes;

using Xunit;

namespace EMMA.Native.Tests;

/// <summary>Tests the playback operations through the library, against the real fixture plugin running as Wasm.</summary>
public sealed class NativePlaybackTests
{
    private const string Episode = "com.example.fixture:e1";
    private const string Track = "com.example.fixture:t1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A plan lists what a node offers, typed and without saying where any of it lives.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task APlanListsWhatANodeOffers()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync("files.example.com").ConfigureAwait(true);
            host.Call("catalog.search", Search("episode"));
            host.Call("catalog.search", Search("track"));

            CallResult video = host.Call("playback.plan", new JsonObject { ["ref"] = Episode });
            CallResult audio = host.Call("playback.plan", new JsonObject { ["ref"] = Track });

            Assert.Equal("stream", video.Ok["primary"]!.GetValue<string>());
            Assert.Equal(["1080p", "480p"], video.Ok["sources"]!.AsArray().Select(source => source!["quality"]!.GetValue<string>()));
            Assert.Equal(1920, video.Ok["sources"]![0]!["width"]!.GetValue<int>());
            Assert.True(video.Ok["sources"]![1]!["is_default"]!.GetValue<bool>());
            Assert.Null(video.Ok["sources"]![1]!["drm_scheme"]);
            Assert.Equal(["en", "ja"], video.Ok["subtitles"]!.AsArray().Select(subtitle => subtitle!["language"]!.GetValue<string>()));
            Assert.Equal("Japanese", video.Ok["audio_tracks"]![0]!["label"]!.GetValue<string>());
            Assert.Equal(["Opening", "Part one"], video.Ok["chapters"]!.AsArray().Select(chapter => chapter!["title"]!.GetValue<string>()));
            Assert.Equal(1_440_000, video.Ok["duration_ms"]!.GetValue<long>());
            Assert.DoesNotContain("example.com/", video.Json.ToJsonString().Replace("com.example.fixture", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Equal("audio", audio.Ok["primary"]!.GetValue<string>());
            Assert.Empty(audio.Ok["audio_tracks"]!.AsArray());
            Assert.Equal(200_000, audio.Ok["duration_ms"]!.GetValue<long>());
        }
    }

    /// <summary>An audio file only the plugin can read is opened on a local address and played from it, whole or by range.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task APluginServedTrackIsPlayableFromTheAddressItIsGiven()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            host.Call("catalog.search", Search("track"));

            CallResult opened = host.Call("playback.open", new JsonObject { ["ref"] = Track, ["role"] = "audio" });
            Uri url = new(opened.Ok["url"]!.GetValue<string>());
            using HttpClient http = new();
            using HttpRequestMessage rangeRequest = new(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(10, 14) } };
            using HttpResponseMessage whole = await http.GetAsync(url, Ct).ConfigureAwait(true);
            using HttpResponseMessage part = await http.SendAsync(rangeRequest, Ct).ConfigureAwait(true);

            Assert.Equal("audio/mpeg", opened.Ok["mime"]!.GetValue<string>());
            Assert.Equal("127.0.0.1", url.Host);
            Assert.Equal(1000, (await whole.Content.ReadAsByteArrayAsync(Ct).ConfigureAwait(true)).Length);
            Assert.Equal([10, 11, 12, 13, 14], await part.Content.ReadAsByteArrayAsync(Ct).ConfigureAwait(true));
            Assert.Equal(new ContentRangeHeaderValue(10, 14, 1000), part.Content.Headers.ContentRange);
        }
    }

    /// <summary>Planning and opening refuse what the API refuses.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task PlanningAndOpeningRefuseWhatCannotBePlayed()
    {
        NativeHarness host = await NativeHarness.StartAsync().ConfigureAwait(true);
        await using (host.ConfigureAwait(true))
        {
            await host.InstallFixtureAsync().ConfigureAwait(true);
            host.Call("catalog.search", Search("series"));

            Assert.Equal("not_found", host.Call("playback.plan", new JsonObject { ["ref"] = Episode }).ErrorCode);
            Assert.Equal("unsupported", host.Call("playback.plan", new JsonObject { ["ref"] = "com.example.fixture:s1" }).ErrorCode);
            Assert.Equal("invalid_argument", host.Call("playback.plan", new JsonObject { ["ref"] = "nonsense" }).ErrorCode);
            Assert.Equal("invalid_argument", host.Call("playback.plan", new JsonObject { ["ref"] = Episode, ["hint"] = string.Empty }).ErrorCode);
            Assert.Equal("invalid_argument", host.Call("playback.open", new JsonObject { ["ref"] = Episode }).ErrorCode);
            Assert.Equal("invalid_argument", host.Call("playback.open", new JsonObject { ["ref"] = Episode, ["role"] = "stream", ["index"] = -1 }).ErrorCode);
            Assert.Equal("invalid_argument", host.Call("playback.open", new JsonObject { ["ref"] = Episode, ["role"] = "stream", ["range"] = new JsonObject() }).ErrorCode);
        }
    }

    private static JsonObject Search(string text) => new()
    {
        ["plugin"] = NativeHarness.Plugin,
        ["query"] = new JsonObject { ["text"] = text },
    };
}
