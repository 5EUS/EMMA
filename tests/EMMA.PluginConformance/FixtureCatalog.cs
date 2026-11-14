using EMMA.Domain;

namespace EMMA.PluginConformance;

/// <summary>
/// The content every conformance subject serves, mirroring <c>native/plugins/fixture</c>: three series, a long
/// chapter list, a recipe with steps, and one proxied asset. Keeping it small and fixed lets one suite
/// run against a real component and an in-memory fake.
/// </summary>
public static class FixtureCatalog
{
    /// <summary>Gets the fixture's declaration.</summary>
    public static PluginInfo Info { get; } = new(
        [
            Kind("series", "Series", ["chapter"], []),
            Kind("chapter", "Chapter", ["page"], []),
            Kind("page", "Page", [], ["image"]),
            Kind("recipe", "Recipe", ["recipe.step"], ["image"]),
            Kind("recipe.step", "Step", [], ["text", "image"]),
            Kind("probe", "Probe", [], ["text", "image", "blob"]),
            Kind("episode", "Episode", [], ["stream", "subtitle", "audio", "chapters"], "emma.profile.video@1"),
            Kind("track", "Track", [], ["audio", "chapters"], "emma.profile.audio@1"),
        ],
        [],
        [new Choice("recent", "Recent", null, true)],
        [],
        []);

    /// <summary>Gets the top-level series, which is also what an empty search returns.</summary>
    public static IReadOnlyList<PluginNode> Series { get; } =
    [
        .. Enumerable.Range(1, 3).Select(i => new PluginNode(
            $"s{i}",
            "series",
            $"Series {i}",
            null,
            null,
            [new Prop("emma.synopsis", PropValue.FromText("A series.")), new Prop("emma.rating", PropValue.FromNumber(7.5))])),
    ];

    /// <summary>Gets the recipes.</summary>
    public static IReadOnlyList<PluginNode> Recipes { get; } =
    [
        new PluginNode("r1", "recipe", "Lasagna", null, null, []),
        new PluginNode("r2", "recipe", "Pancakes", null, null, []),
    ];

    /// <summary>Gets the playable video node.</summary>
    public static IReadOnlyList<PluginNode> Episodes { get; } = [new PluginNode("e1", "episode", "Episode 1", null, null, [])];

    /// <summary>Gets the playable audio node.</summary>
    public static IReadOnlyList<PluginNode> Tracks { get; } = [new PluginNode("t1", "track", "Track 1", null, null, [])];

    /// <summary>Gets the number of chapters of the first series.</summary>
    public static int ChapterCount => 120;

    /// <summary>Gets the length of the bytes behind the proxied token <c>tok-1</c>.</summary>
    public static int ProxiedLength => 1000;

    /// <summary>Gets the bytes behind a proxied token: the value at index i is <c>i % 251</c>.</summary>
    /// <param name="length">How many bytes.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

    /// <summary>Lists the children of a node the way the fixture does, or nothing for an unknown node.</summary>
    /// <param name="parent">The parent id, or <see langword="null"/> for the root.</param>
    /// <returns>The children, or <see langword="null"/> when the node is unknown.</returns>
    public static IReadOnlyList<PluginNode>? ChildrenOf(string? parent) => parent switch
    {
        null => Series,
        "s1" => [.. Enumerable.Range(1, ChapterCount).Select(i => new PluginNode($"s1-c{i}", "chapter", $"Chapter {i}", "s1", null, []))],
        "r1" =>
        [
            new PluginNode("r1-s1", "recipe.step", "Make the sauce", "r1", null, []),
            new PluginNode("r1-s2", "recipe.step", "Layer it", "r1", null, []),
        ],
        _ when parent.Contains("-c", StringComparison.Ordinal) =>
            [.. Enumerable.Range(1, 5).Select(i => new PluginNode($"{parent}-p{i}", "page", $"Page {i}", parent, null, []))],
        _ => null,
    };

    /// <summary>Gets what the fixture resolves a playable node to, mirroring <c>native/plugins/fixture</c>.</summary>
    /// <param name="node">The plugin-local id.</param>
    /// <param name="role">The role.</param>
    /// <returns>The assets, or <see langword="null"/> when the node has nothing for the role.</returns>
    public static IReadOnlyList<Asset>? PlaybackAssets(string node, string role) => (node, role) switch
    {
        ("e1", "stream") =>
        [
            Media("application/vnd.apple.mpegurl", "e1/master.m3u8", Text("emma.quality", "1080p"), Whole("emma.width", 1920), Whole("emma.height", 1080), Whole("emma.bitrate", 5_000_000), Whole("emma.duration-ms", 1_440_000)),
            Media("video/mp4", "e1.mp4", Text("emma.quality", "480p"), new Prop("emma.default", PropValue.FromFlag(true)), Text("emma.drm.scheme", "none")),
        ],
        ("e1", "subtitle") =>
        [
            Media("text/vtt", "e1/en.vtt", Text("emma.language", "en"), Text("emma.label", "English"), new Prop("emma.default", PropValue.FromFlag(true))),
            Media("text/vtt", "e1/ja.vtt", Text("emma.language", "ja")),
        ],
        ("e1", "audio") => [Media("audio/mp4", "e1/ja.m4a", Text("emma.language", "ja"), Text("emma.label", "Japanese"))],
        ("e1", "chapters") => [Marker(0, 90_000, "Opening"), Marker(90_000, null, "Part one")],
        ("t1", "audio") => [new Asset("audio/mpeg", new ProxiedDelivery("tok-1"), ProxiedLength, [Whole("emma.duration-ms", 200_000)])],
        ("t1", "chapters") => [Marker(0, 100_000, "Side A"), Marker(100_000, 200_000, "Side B")],
        _ => null,
    };

    private static Prop Text(string key, string value) => new(key, PropValue.FromText(value));

    private static Prop Whole(string key, long value) => new(key, PropValue.FromWholeNumber(value));

    private static Asset Media(string mime, string path, params Prop[] props) => new(
        mime,
        new RemoteDelivery(new UriBuilder("https", "files.example.com", 443, path).Uri, [new HttpHeader("referer", "https://example.com/")], null),
        null,
        props);

    private static Asset Marker(long start, long? end, string title) => new(
        "text/plain",
        new InlineDelivery(Array.Empty<byte>()),
        null,
        [Whole("emma.start-ms", start), Text("emma.label", title), .. end is { } stop ? new[] { Whole("emma.end-ms", stop) } : []]);

    private static KindInfo Kind(string kind, string label, string[] children, string[] roles, string? profile = null) => new(
        NodeKind.Create(kind),
        label,
        [.. children.Select(NodeKind.Create)],
        [.. roles.Select(AssetRole.Create)],
        profile);
}
