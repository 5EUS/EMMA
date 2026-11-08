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

    private static KindInfo Kind(string kind, string label, string[] children, string[] roles) => new(
        NodeKind.Create(kind),
        label,
        [.. children.Select(NodeKind.Create)],
        [.. roles.Select(AssetRole.Create)],
        null);
}
