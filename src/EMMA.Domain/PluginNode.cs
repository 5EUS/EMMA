namespace EMMA.Domain;

/// <summary>
/// A node exactly as a plugin returned it: raw strings, not yet validated. The
/// application layer validates it against the plugin's declared kinds and the
/// contract limits, then turns it into a <see cref="Node"/>.
/// </summary>
/// <param name="Id">The plugin-local id.</param>
/// <param name="Kind">The kind string.</param>
/// <param name="Title">The title.</param>
/// <param name="Parent">The plugin-local parent id, if the plugin supplied one.</param>
/// <param name="Thumbnail">A thumbnail URL, if any.</param>
/// <param name="Props">Descriptive data.</param>
public sealed record PluginNode(
    string Id,
    string Kind,
    string Title,
    string? Parent,
    string? Thumbnail,
    IReadOnlyList<Prop> Props);
