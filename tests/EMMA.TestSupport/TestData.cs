using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>Builders for the plugin, ids and declarations used across the test suites.</summary>
public static class TestData
{
    /// <summary>Gets the plugin id used by default.</summary>
    public static PluginId Plugin { get; } = PluginId.Create("com.example.test");

    /// <summary>Gets a second plugin id, for tests that involve two plugins.</summary>
    public static PluginId OtherPlugin { get; } = PluginId.Create("com.example.other");

    /// <summary>Gets a user id.</summary>
    public static UserId User { get; } = UserId.Create("alice");

    /// <summary>Builds an absolute URL without a string literal.</summary>
    /// <param name="host">The host.</param>
    /// <param name="path">The path, starting with a slash.</param>
    /// <param name="scheme">The scheme.</param>
    /// <returns>The URL.</returns>
    public static Uri Url(string host, string path = "/", string scheme = "https") => new UriBuilder(scheme, host, -1, path).Uri;

    /// <summary>Builds a node reference owned by <see cref="Plugin"/>.</summary>
    /// <param name="id">The local id.</param>
    /// <returns>The reference.</returns>
    public static NodeRef Ref(string id) => new(Plugin, LocalNodeId.Create(id));

    /// <summary>Builds a raw plugin node.</summary>
    /// <param name="id">The local id.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="title">The title.</param>
    /// <param name="parent">The parent id, if any.</param>
    /// <returns>The node.</returns>
    public static PluginNode RawNode(string id, string kind = "series", string title = "Title", string? parent = null) =>
        new(id, kind, title, parent, null, []);

    /// <summary>Builds a raw page.</summary>
    /// <param name="nextCursor">The next cursor.</param>
    /// <param name="items">The nodes.</param>
    /// <returns>The page.</returns>
    public static PluginNodePage RawPage(string? nextCursor, params PluginNode[] items) => new(items, nextCursor, null, []);

    /// <summary>Builds a kind declaration.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="children">Child kinds.</param>
    /// <param name="roles">Resolvable roles.</param>
    /// <returns>The declaration.</returns>
    public static KindInfo Kind(string kind, string[]? children = null, string[]? roles = null) => new(
        NodeKind.Create(kind),
        kind,
        [.. (children ?? []).Select(NodeKind.Create)],
        [.. (roles ?? []).Select(AssetRole.Create)],
        null);

    /// <summary>
    /// Builds the declaration of a small three-level tree (<c>series</c>, <c>chapter</c>,
    /// <c>page</c>) with an <c>image</c> role on pages. It stands in for any plugin; the
    /// names carry no meaning to the code under test.
    /// </summary>
    /// <param name="filters">Declared filters.</param>
    /// <param name="sorts">Declared sorts.</param>
    /// <returns>The declaration.</returns>
    public static PluginInfo Info(IReadOnlyList<FilterSpec>? filters = null, IReadOnlyList<Choice>? sorts = null) => new(
        [Kind("series", ["chapter"]), Kind("chapter", ["page"]), Kind("page", roles: ["image"])],
        filters ?? [],
        sorts ?? [],
        [],
        []);

    /// <summary>Builds a simple filter declaration.</summary>
    /// <param name="id">The filter id.</param>
    /// <param name="type">The filter type.</param>
    /// <param name="required">Whether it is required.</param>
    /// <param name="choices">Allowed choices.</param>
    /// <returns>The declaration.</returns>
    public static FilterSpec Filter(string id, FilterType type, bool required = false, params string[] choices) => new(
        id,
        id,
        type,
        [],
        required,
        false,
        null,
        null,
        [.. choices.Select(choice => new Choice(choice, choice, null, false))],
        null,
        null);

    /// <summary>Builds a query that only has text.</summary>
    /// <param name="text">The search text.</param>
    /// <returns>The query.</returns>
    public static Query TextQuery(string text) => new(text, [], []);
}
