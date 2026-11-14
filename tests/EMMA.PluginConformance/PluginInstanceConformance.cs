using EMMA.Domain;
using EMMA.PluginHost.Instances;

using Xunit;

namespace EMMA.PluginConformance;

/// <summary>
/// What every <see cref="IPluginInstance"/> must do, serving <see cref="FixtureCatalog"/>. Derive from this in a
/// transport's test project and implement <see cref="CreateAsync"/>; the tests are inherited.
/// </summary>
public abstract class PluginInstanceConformance
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The handshake declares kinds whose children and roles are consistent.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task DescribeDeclaresAConsistentTree()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginInfo info = (await instance.DescribeAsync(Ct).ConfigureAwait(true)).Outcome.Value;

                Assert.NotEmpty(info.Kinds);
                Assert.All(info.Kinds.SelectMany(kind => kind.Children), child => Assert.NotNull(info.FindKind(child)));
                Assert.Equal([AssetRole.Create("image")], info.FindKind(NodeKind.Create("page"))!.Roles);
                Assert.Empty(info.FindKind(NodeKind.Create("series"))!.Roles);
            });
    }

    /// <summary>The health probe answers.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task HealthAnswers()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginCall<PluginHealth> call = await instance.HealthAsync(Ct).ConfigureAwait(true);

                Assert.True(call.Outcome.Value.Ok);
            });
    }

    /// <summary>Search pages by cursor, with a total, and an empty last cursor.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task SearchPagesByCursor()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginNodePage first = (await instance.SearchAsync(Text("series", 2), Ct).ConfigureAwait(true)).Outcome.Value;
                PluginNodePage second = (await instance.SearchAsync(Text("series", 2, first.NextCursor), Ct).ConfigureAwait(true)).Outcome.Value;

                Assert.Equal(["Series 1", "Series 2"], first.Items.Select(node => node.Title));
                Assert.Equal(3, first.Total);
                Assert.NotNull(first.NextCursor);
                Assert.Equal(["Series 3"], second.Items.Select(node => node.Title));
                Assert.Null(second.NextCursor);
            });
    }

    /// <summary>Props are typed and keep their values.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task PropsKeepTheirTypes()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginNode node = (await instance.SearchAsync(Text("series", 1), Ct).ConfigureAwait(true)).Outcome.Value.Items.Single();

                Assert.Equal(PropValue.FromText("A series."), node.Props.Single(prop => string.Equals(prop.Key, "emma.synopsis", StringComparison.Ordinal)).Value);
                Assert.Equal(PropValue.FromNumber(7.5), node.Props.Single(prop => string.Equals(prop.Key, "emma.rating", StringComparison.Ordinal)).Value);
            });
    }

    /// <summary>Children walk down the tree, report a total, and an unknown parent is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ChildrenWalkDownAndUnknownParentsAreNotFound()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginNodePage chapters = (await instance.ChildrenAsync(LocalNodeId.Create("s1"), new PageRequest(null, 3), Ct).ConfigureAwait(true)).Outcome.Value;
                PluginNodePage pages = (await instance.ChildrenAsync(LocalNodeId.Create("s1-c2"), new PageRequest(), Ct).ConfigureAwait(true)).Outcome.Value;
                PluginNodePage root = (await instance.ChildrenAsync(null, new PageRequest(), Ct).ConfigureAwait(true)).Outcome.Value;
                PluginCall<PluginNodePage> missing = await instance.ChildrenAsync(LocalNodeId.Create("nope"), new PageRequest(), Ct).ConfigureAwait(true);

                Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], chapters.Items.Select(node => node.Title));
                Assert.Equal(FixtureCatalog.ChapterCount, chapters.Total);
                Assert.All(pages.Items, node => Assert.Equal("s1-c2", node.Parent));
                Assert.Equal(3, root.Items.Count);
                Assert.Equal(ErrorCode.NotFound, missing.Outcome.Error?.Code);
                Assert.False(missing.Trapped);
            });
    }

    /// <summary>Get returns known nodes in request order and omits unknown ones.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task GetKeepsRequestOrderAndOmitsUnknownIds()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                LocalNodeId[] ids = [LocalNodeId.Create("s2"), LocalNodeId.Create("unknown"), LocalNodeId.Create("r1")];

                PluginCall<IReadOnlyList<PluginNode>> call = await instance.GetAsync(ids, Ct).ConfigureAwait(true);

                Assert.Equal(["Series 2", "Lasagna"], call.Outcome.Value.Select(node => node.Title));
            });
    }

    /// <summary>Resolve returns remote, inline and proxied deliveries, and refuses a node with nothing for the role.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ResolveReturnsEachDeliveryKind()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                Asset remote = (await instance.ResolveAsync(Resolve("s1-c1-p1", "image"), Ct).ConfigureAwait(true)).Outcome.Value.Single();
                Asset inline = (await instance.ResolveAsync(Resolve("inline1", "text"), Ct).ConfigureAwait(true)).Outcome.Value.Single();
                Asset proxied = (await instance.ResolveAsync(Resolve("proxied1", "image"), Ct).ConfigureAwait(true)).Outcome.Value.Single();
                PluginCall<IReadOnlyList<Asset>> unsupported = await instance.ResolveAsync(Resolve("s1", "image"), Ct).ConfigureAwait(true);

                RemoteDelivery delivery = Assert.IsType<RemoteDelivery>(remote.Delivery);
                Assert.Equal("files.example.com", delivery.Url.Host);
                Assert.Equal(new HttpHeader("referer", "https://example.com/"), delivery.Headers.Single());
                Assert.Equal("image/png", remote.Mime);
                Assert.Equal("hello"u8.ToArray(), Assert.IsType<InlineDelivery>(inline.Delivery).Payload.ToArray());
                Assert.Equal("tok-1", Assert.IsType<ProxiedDelivery>(proxied.Delivery).Token);
                Assert.Equal(ErrorCode.Unsupported, unsupported.Outcome.Error?.Code);
            });
    }

    /// <summary>
    /// A plugin that follows the video or audio profile declares it and the roles it resolves, and what it resolves carries the
    /// well-known props with the types the contract gives them.
    /// </summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task PlayableNodesFollowTheVideoAndAudioProfiles()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginInfo info = (await instance.DescribeAsync(Ct).ConfigureAwait(true)).Outcome.Value;
                IReadOnlyList<Asset> streams = (await instance.ResolveAsync(Resolve("e1", "stream"), Ct).ConfigureAwait(true)).Outcome.Value;
                IReadOnlyList<Asset> subtitles = (await instance.ResolveAsync(Resolve("e1", "subtitle"), Ct).ConfigureAwait(true)).Outcome.Value;
                IReadOnlyList<Asset> alternates = (await instance.ResolveAsync(Resolve("e1", "audio"), Ct).ConfigureAwait(true)).Outcome.Value;
                IReadOnlyList<Asset> markers = (await instance.ResolveAsync(Resolve("e1", "chapters"), Ct).ConfigureAwait(true)).Outcome.Value;
                IReadOnlyList<Asset> track = (await instance.ResolveAsync(Resolve("t1", "audio"), Ct).ConfigureAwait(true)).Outcome.Value;

                Assert.Equal("emma.profile.video@1", info.FindKind(NodeKind.Create("episode"))!.Profile);
                Assert.Equal("emma.profile.audio@1", info.FindKind(NodeKind.Create("track"))!.Profile);
                Assert.Equal(["stream", "subtitle", "audio", "chapters"], info.FindKind(NodeKind.Create("episode"))!.Roles.Select(role => role.Value));
                Assert.Equal(["application/vnd.apple.mpegurl", "video/mp4"], streams.Select(asset => asset.Mime));
                Assert.Equal(PropValue.FromText("1080p"), Prop(streams[0], "emma.quality"));
                Assert.Equal(PropValue.FromWholeNumber(1920), Prop(streams[0], "emma.width"));
                Assert.Equal(PropValue.FromWholeNumber(1_440_000), Prop(streams[0], "emma.duration-ms"));
                Assert.Equal(PropValue.FromFlag(true), Prop(streams[1], "emma.default"));
                Assert.Equal(PropValue.FromText("none"), Prop(streams[1], "emma.drm.scheme"));
                Assert.Equal(["en", "ja"], subtitles.Select(asset => Prop(asset, "emma.language").Text));
                Assert.Equal(PropValue.FromText("Japanese"), Prop(alternates.Single(), "emma.label"));
                Assert.Equal([0L, 90_000L], markers.Select(asset => Prop(asset, "emma.start-ms").WholeNumber));
                Assert.Null(markers[1].Props.SingleOrDefault(prop => string.Equals(prop.Key, "emma.end-ms", StringComparison.Ordinal)));
                Assert.Equal("tok-1", Assert.IsType<ProxiedDelivery>(track.Single().Delivery).Token);
                Assert.Equal("audio/mpeg", track.Single().Mime);
            });
    }

    /// <summary>Read returns the bytes, honours ranges and reports the total; an unknown token is not found.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ReadHonoursRangesAndUnknownTokensAreNotFound()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                Chunk whole = (await instance.ReadAsync("tok-1", null, Ct).ConfigureAwait(true)).Outcome.Value;
                Chunk part = (await instance.ReadAsync("tok-1", new ByteRange(10, 5), Ct).ConfigureAwait(true)).Outcome.Value;
                Chunk tail = (await instance.ReadAsync("tok-1", new ByteRange(995, null), Ct).ConfigureAwait(true)).Outcome.Value;
                PluginCall<Chunk> missing = await instance.ReadAsync("nope", null, Ct).ConfigureAwait(true);

                Assert.Equal(FixtureCatalog.Pattern(FixtureCatalog.ProxiedLength), whole.Bytes.ToArray());
                Assert.Equal(FixtureCatalog.ProxiedLength, whole.Total);
                Assert.Equal(new byte[] { 10, 11, 12, 13, 14 }, part.Bytes.ToArray());
                Assert.Equal(5, tail.Bytes.Length);
                Assert.Equal(ErrorCode.NotFound, missing.Outcome.Error?.Code);
            });
    }

    /// <summary>A plugin failure keeps its code, message and retry delay, and is not a trap.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task PluginFailuresKeepTheirCodeAndRetryDelay()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginCall<PluginNodePage> limited = await instance.SearchAsync(Text("error:rate-limited"), Ct).ConfigureAwait(true);
                PluginCall<PluginNodePage> unauthorized = await instance.SearchAsync(Text("error:unauthorized"), Ct).ConfigureAwait(true);

                Assert.Equal(ErrorCode.RateLimited, limited.Outcome.Error?.Code);
                Assert.Equal(TimeSpan.FromMilliseconds(1500), limited.Outcome.Error?.RetryAfter);
                Assert.True(limited.Outcome.Error!.IsTransient);
                Assert.Equal(ErrorCode.Unauthorized, unauthorized.Outcome.Error?.Code);
                Assert.False(limited.Trapped);
            });
    }

    /// <summary>A crashing plugin is reported as trapped with an internal failure, and the instance keeps working.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ACrashIsReportedAsTrappedAndTheInstanceStaysUsable()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginCall<PluginNodePage> crashed = await instance.SearchAsync(Text("trap"), Ct).ConfigureAwait(true);
                PluginCall<PluginNodePage> after = await instance.SearchAsync(Text("series"), Ct).ConfigureAwait(true);

                Assert.True(crashed.Trapped);
                Assert.Equal(ErrorCode.Internal, crashed.Outcome.Error?.Code);
                Assert.True(after.Outcome.IsSuccess);
                Assert.False(after.Trapped);
            });
    }

    /// <summary>The search-assist extension is advertised exactly when it works; without it the calls are unsupported.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ExtensionsAreAdvertisedExactlyWhenTheyWork()
    {
        return this.WithInstanceAsync(
            assisted: true,
            assistedInstance => this.WithInstanceAsync(
                assisted: false,
                async plain =>
                {
                    PluginCall<IReadOnlyList<Choice>> suggestions = await assistedInstance.SuggestAsync("tag", "ber", 2, Ct).ConfigureAwait(true);
                    PluginCall<IReadOnlyList<PluginNode>> enriched = await assistedInstance.EnrichAsync([LocalNodeId.Create("s1")], Ct).ConfigureAwait(true);
                    PluginCall<IReadOnlyList<Choice>> unsupported = await plain.SuggestAsync("tag", "ber", 2, Ct).ConfigureAwait(true);
                    PluginCall<IReadOnlyList<PluginNode>> unenriched = await plain.EnrichAsync([LocalNodeId.Create("s1")], Ct).ConfigureAwait(true);

                    Assert.Contains(PluginExtensions.SearchAssist, assistedInstance.Extensions);
                    Assert.DoesNotContain(PluginExtensions.SearchAssist, plain.Extensions);
                    Assert.Equal(["ber 1", "ber 2"], suggestions.Outcome.Value.Select(choice => choice.Label));
                    Assert.Equal("tag:ber-1", suggestions.Outcome.Value[0].Value);
                    Assert.Equal("Enriched s1", enriched.Outcome.Value.Single().Title);
                    Assert.Equal(ErrorCode.Unsupported, unsupported.Outcome.Error?.Code);
                    Assert.Equal(ErrorCode.Unsupported, unenriched.Outcome.Error?.Code);
                }));
    }

    /// <summary>Concurrent calls do not interfere with each other.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ConcurrentCallsAreIndependent()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                PluginCall<PluginNodePage>[] calls = await Task.WhenAll(
                    Enumerable.Range(0, 16).Select(i => instance.SearchAsync(Text(i % 2 == 0 ? "series" : "recipe"), Ct))).ConfigureAwait(true);

                Assert.All(calls.Where((_, i) => i % 2 == 0), call => Assert.Equal(3, call.Outcome.Value.Items.Count));
                Assert.All(calls.Where((_, i) => i % 2 == 1), call => Assert.Equal(2, call.Outcome.Value.Items.Count));
            });
    }

    /// <summary>A token cancelled before the call starts stops it.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public Task ACancelledTokenStopsTheCallBeforeItStarts()
    {
        return this.WithInstanceAsync(
            assisted: false,
            async instance =>
            {
                using CancellationTokenSource source = new();
                await source.CancelAsync().ConfigureAwait(true);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => instance.SearchAsync(Text("series"), source.Token)).ConfigureAwait(true);
            });
    }

    /// <summary>Disposing twice is harmless.</summary>
    /// <returns>A task that completes when the test has run.</returns>
    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        IPluginInstance instance = await this.CreateAsync(searchAssist: false).ConfigureAwait(true);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await instance.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Starts a fresh instance serving the fixture catalog.</summary>
    /// <param name="searchAssist">Whether the instance must offer the search-assist extension.</param>
    /// <returns>The running instance; the test disposes it.</returns>
    protected abstract Task<IPluginInstance> CreateAsync(bool searchAssist);

    private static Query Text(string text, int? limit = null, string? cursor = null) => new(text, [], [], null, cursor, limit);

    private static PropValue Prop(Asset asset, string key) =>
        asset.Props.Single(prop => string.Equals(prop.Key, key, StringComparison.Ordinal)).Value;

    private static ResolveRequest Resolve(string node, string role) => new(LocalNodeId.Create(node), AssetRole.Create(role), []);

    private async Task WithInstanceAsync(bool assisted, Func<IPluginInstance, Task> test)
    {
        IPluginInstance instance = await this.CreateAsync(assisted).ConfigureAwait(true);
        await using (instance.ConfigureAwait(true))
        {
            await test(instance).ConfigureAwait(true);
        }
    }
}
