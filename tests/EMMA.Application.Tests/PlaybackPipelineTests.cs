using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.InMemory;
using EMMA.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace EMMA.Application.Tests;

/// <summary>Tests for <see cref="PlaybackPipeline"/> and the video and audio profile it reads.</summary>
public sealed class PlaybackPipelineTests
{
    private readonly FakeTimeProvider time = new();
    private readonly FakePluginGateway gateway = new();
    private readonly InMemoryNodeStore nodes = new();
    private readonly PlaybackPipeline playback;

    /// <summary>Initializes a new instance of the <see cref="PlaybackPipelineTests"/> class.</summary>
    public PlaybackPipelineTests()
    {
        PluginCallExecutor executor = new(new PluginCallOptions(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(30)), this.time);
        AssetPipeline assets = new(
            this.gateway,
            new FakePolicies().Allow(TestData.Plugin, "cdn.example.com"),
            this.nodes,
            new FakeAssetFetcher(this.time),
            new BoundedAssetCache(new AssetCacheOptions(1024 * 1024, 0, "unused", TimeSpan.FromDays(1)), this.time),
            new BoundedResolutionCache(64, this.time),
            executor,
            new AssetPipelineOptions(1024),
            this.time);
        this.playback = new PlaybackPipeline(new CatalogPipeline(this.gateway, this.nodes, new InMemoryNodeListingStore(), executor), assets, this.nodes);
        this.gateway.OnDescribe = _ => Outcome.Success(new PluginInfo(
            [
                TestData.Kind("show", ["episode"]),
                TestData.Kind("episode", roles: ["stream", "subtitle", "audio", "chapters"]),
                TestData.Kind("track", roles: ["audio", "chapters"]),
                TestData.Kind("picture", roles: ["image"]),
            ],
            [],
            [],
            [],
            []));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stream node's plan has its qualities, subtitles, alternate audio and chapters, typed.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AStreamNodeIsPlannedWithEverythingItOffers()
    {
        await this.SeedAsync("ep1", "episode").ConfigureAwait(true);
        this.Resolves(
            ("stream", [Remote("application/vnd.apple.mpegurl", Text("emma.quality", "1080p"), Whole("emma.width", 1920), Whole("emma.height", 1080), Whole("emma.duration-ms", 1_440_000), Whole("emma.bitrate", 5_000_000)), Remote("video/mp4", Text("emma.quality", "480p"), Flag("emma.default", true))]),
            ("subtitle", [Remote("text/vtt", Text("emma.language", "en"), Text("emma.label", "English"), Flag("emma.default", true)), Remote("text/vtt", Text("emma.language", "ja"))]),
            ("audio", [Remote("audio/mp4", Text("emma.language", "ja"), Text("emma.label", "Original"))]),
            ("chapters", [Marker(0, 90_000, "Opening"), Marker(90_000, null, "Part one")]));

        PlaybackPlan plan = (await this.playback.PlanAsync(TestData.Ref("ep1"), null, Ct).ConfigureAwait(true)).Value;

        Assert.Equal("stream", plan.Primary.Value);
        Assert.Equal(["1080p", "480p"], plan.Sources.Select(source => source.Quality));
        Assert.Equal((1920, 1080, 5_000_000L), (plan.Sources[0].Width, plan.Sources[0].Height, plan.Sources[0].Bitrate));
        Assert.True(plan.Sources[1].IsDefault);
        Assert.Equal([0, 1], plan.Sources.Select(source => source.Index));
        Assert.Equal(["en", "ja"], plan.Subtitles.Select(subtitle => subtitle.Language));
        Assert.Equal("English", plan.Subtitles[0].Label);
        Assert.Equal("subtitle", plan.Subtitles[0].Role.Value);
        Assert.Equal("Original", plan.AudioTracks.Single().Label);
        Assert.Equal(["Opening", "Part one"], plan.Chapters.Select(chapter => chapter.Title));
        Assert.Equal(90_000, plan.Chapters[0].EndMs);
        Assert.Null(plan.Chapters[1].EndMs);
        Assert.Equal(1_440_000, plan.DurationMs);
    }

    /// <summary>For a node whose own media is audio, the audio assets are the sources and there are no alternate tracks.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task AnAudioNodeIsItsOwnPrimaryRole()
    {
        await this.SeedAsync("t1", "track").ConfigureAwait(true);
        this.Resolves(
            ("audio", [Remote("audio/mpeg", Whole("emma.duration-ms", 200_000))]),
            ("chapters", [Marker(0, 100_000, "Side A")]));

        PlaybackPlan plan = (await this.playback.PlanAsync(TestData.Ref("t1"), null, Ct).ConfigureAwait(true)).Value;

        Assert.Equal("audio", plan.Primary.Value);
        Assert.Equal(["audio/mpeg"], plan.Sources.Select(source => source.Mime));
        Assert.Empty(plan.AudioTracks);
        Assert.Empty(plan.Subtitles);
        Assert.Single(plan.Chapters);
        Assert.Equal(200_000, plan.DurationMs);
    }

    /// <summary>With no length on a source, the last chapter that has an end gives it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheLengthFallsBackToTheLastChapterEnd()
    {
        await this.SeedAsync("t1", "track").ConfigureAwait(true);
        this.Resolves(("audio", [Remote("audio/mpeg")]), ("chapters", [Marker(0, 100_000, "A"), Marker(100_000, 250_000, "B")]));

        PlaybackPlan plan = (await this.playback.PlanAsync(TestData.Ref("t1"), null, Ct).ConfigureAwait(true)).Value;

        Assert.Equal(250_000, plan.DurationMs);
    }

    /// <summary>DRM is reported only when the media is protected, with where to ask for a license.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task ProtectedMediaIsReportedAndOpenMediaIsNot()
    {
        await this.SeedAsync("ep1", "episode").ConfigureAwait(true);
        this.Resolves(("stream", [
            Remote("application/dash+xml", Text("emma.drm.scheme", "widevine"), new Prop("emma.drm.license-url", PropValue.FromUrl(TestData.Url("license.example.com", "/w")))),
            Remote("application/dash+xml", Text("emma.drm.scheme", "none"), Flag("emma.live", true)),
            Remote("video/mp4")]));

        PlaybackPlan plan = (await this.playback.PlanAsync(TestData.Ref("ep1"), null, Ct).ConfigureAwait(true)).Value;

        Assert.Equal("widevine", plan.Sources[0].DrmScheme);
        Assert.Equal("https://license.example.com/w", plan.Sources[0].DrmLicenseUrl?.AbsoluteUri);
        Assert.Null(plan.Sources[1].DrmScheme);
        Assert.Null(plan.Sources[1].DrmLicenseUrl);
        Assert.True(plan.Sources[1].IsLive);
        Assert.False(plan.Sources[0].IsLive);
        Assert.Null(plan.Sources[2].DrmScheme);
    }

    /// <summary>Only the node's own media is asked for a quality; the rest of the plan does not depend on it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task TheHintGoesToTheNodesOwnMediaOnly()
    {
        await this.SeedAsync("ep1", "episode").ConfigureAwait(true);
        List<(string Role, int Hints)> asked = [];
        this.gateway.OnResolve = (_, request) =>
        {
            asked.Add((request.Role.Value, request.Hints.Count));
            return Outcome.Success<IReadOnlyList<Asset>>([Remote("video/mp4")]);
        };

        await this.playback.PlanAsync(TestData.Ref("ep1"), "720p", Ct).ConfigureAwait(true);

        Assert.Contains(("stream", 1), asked);
        Assert.All(asked.Where(call => !string.Equals(call.Role, "stream", StringComparison.Ordinal)), call => Assert.Equal(0, call.Hints));
    }

    /// <summary>A subtitle list that cannot be had does not stop the video; a stream that cannot be had does.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task OnlyTheNodesOwnMediaIsRequired()
    {
        await this.SeedAsync("ep1", "episode").ConfigureAwait(true);
        this.gateway.OnResolve = (_, request) => string.Equals(request.Role.Value, "stream", StringComparison.Ordinal)
            ? Outcome.Success<IReadOnlyList<Asset>>([Remote("video/mp4")])
            : Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.Unavailable, "down");
        Outcome<PlaybackPlan> partial = await this.playback.PlanAsync(TestData.Ref("ep1"), null, Ct).ConfigureAwait(true);

        await this.SeedAsync("ep2", "episode").ConfigureAwait(true);
        this.gateway.OnResolve = (_, _) => Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.Unauthorized, "log in");
        Outcome<PlaybackPlan> none = await this.playback.PlanAsync(TestData.Ref("ep2"), null, Ct).ConfigureAwait(true);

        Assert.Single(partial.Value.Sources);
        Assert.Empty(partial.Value.Subtitles);
        Assert.Equal(ErrorCode.Unauthorized, none.Error?.Code);
    }

    /// <summary>A node the host never saw, and a node that has nothing to play, are refused.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task NodesThatCannotBePlayedAreRefused()
    {
        await this.SeedAsync("pic", "picture").ConfigureAwait(true);

        Outcome<PlaybackPlan> unseen = await this.playback.PlanAsync(TestData.Ref("nope"), null, Ct).ConfigureAwait(true);
        Outcome<PlaybackPlan> nothing = await this.playback.PlanAsync(TestData.Ref("pic"), null, Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.NotFound, unseen.Error?.Code);
        Assert.Equal(ErrorCode.Unsupported, nothing.Error?.Code);
    }

    /// <summary>A well-known prop of the wrong type, or with a nonsense value, makes the plugin's answer a contract violation.</summary>
    /// <param name="key">The prop key.</param>
    /// <param name="kind">Which wrong value to send.</param>
    /// <returns>A task that completes when the test has run.</returns>
    [Theory]
    [InlineData("emma.width", "text")]
    [InlineData("emma.quality", "number")]
    [InlineData("emma.live", "text")]
    [InlineData("emma.default", "number")]
    [InlineData("emma.drm.license-url", "text")]
    [InlineData("emma.duration-ms", "negative")]
    [InlineData("emma.start-ms", "negative")]
    [InlineData("emma.language", "empty")]
    public async Task WrongTypedWellKnownPropsAreRejected(string key, string kind)
    {
        await this.SeedAsync("ep1", "episode").ConfigureAwait(true);
        PropValue value = kind switch
        {
            "text" => PropValue.FromText("x"),
            "number" => PropValue.FromNumber(1.5),
            "negative" => PropValue.FromWholeNumber(-1),
            _ => PropValue.FromText(string.Empty),
        };
        this.Resolves(("stream", [Remote("video/mp4", new Prop(key, value))]));

        Outcome<PlaybackPlan> plan = await this.playback.PlanAsync(TestData.Ref("ep1"), null, Ct).ConfigureAwait(true);

        Assert.Equal(ErrorCode.Internal, plan.Error?.Code);
        Assert.Contains(key, plan.Error?.Message, StringComparison.Ordinal);
    }

    /// <summary>A chapter marker without a start, or that ends before it starts, is a contract violation and leaves the plan without chapters.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task BadChapterMarkersAreLeftOut()
    {
        await this.SeedAsync("t1", "track").ConfigureAwait(true);
        this.Resolves(("audio", [Remote("audio/mpeg")]), ("chapters", [Remote("text/plain", Text("emma.label", "No start"))]));
        Outcome<PlaybackPlan> noStart = await this.playback.PlanAsync(TestData.Ref("t1"), null, Ct).ConfigureAwait(true);
        this.Resolves(("audio", [Remote("audio/mpeg")]), ("chapters", [Marker(5000, 1000, "Backwards")]));
        Outcome<PlaybackPlan> backwards = await this.playback.PlanAsync(TestData.Ref("t1"), null, Ct).ConfigureAwait(true);

        Assert.Empty(noStart.Value.Chapters);
        Assert.Empty(backwards.Value.Chapters);
        Assert.False(PlaybackProfile.Chapters(TestData.Plugin, [Remote("text/plain")]).IsSuccess);
        Assert.Equal("Chapter 1", PlaybackProfile.Chapters(TestData.Plugin, [Remote("text/plain", Whole("emma.start-ms", 0))]).Value.Single().Title);
    }

    private static Prop Text(string key, string value) => new(key, PropValue.FromText(value));

    private static Prop Whole(string key, long value) => new(key, PropValue.FromWholeNumber(value));

    private static Prop Flag(string key, bool value) => new(key, PropValue.FromFlag(value));

    private static Asset Remote(string mime, params Prop[] props) =>
        new(mime, new RemoteDelivery(TestData.Url("cdn.example.com", "/a"), [], null), null, props);

    private static Asset Marker(long start, long? end, string title) =>
        new("text/plain", new InlineDelivery(Array.Empty<byte>()), null, [Whole("emma.start-ms", start), .. end is { } stop ? new[] { Whole("emma.end-ms", stop) } : [], Text("emma.label", title)]);

    private Task SeedAsync(string id, string kind) =>
        this.nodes.UpsertAsync([new Node(TestData.Ref(id), NodeKind.Create(kind), "Title", null, null, [])], CancellationToken.None);

    private void Resolves(params (string Role, Asset[] Assets)[] byRole) =>
        this.gateway.OnResolve = (_, request) =>
        {
            (string Role, Asset[] Assets) found = Array.Find(byRole, entry => string.Equals(entry.Role, request.Role.Value, StringComparison.Ordinal));
            return found.Assets is null
                ? Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.Unsupported, "none")
                : Outcome.Success<IReadOnlyList<Asset>>(found.Assets);
        };
}
