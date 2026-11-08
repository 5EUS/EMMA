namespace EMMA.Domain;

/// <summary>A search request.</summary>
/// <param name="Text">The free text; may be empty for a browse-everything request.</param>
/// <param name="Kinds">Restrict results to these kinds; empty means all.</param>
/// <param name="Filters">The chosen filter values.</param>
/// <param name="Sort">One of the plugin's declared sorts.</param>
/// <param name="Cursor">The previous page's next cursor.</param>
/// <param name="Limit">A hint for the page size.</param>
public sealed record Query(
    string Text,
    IReadOnlyList<NodeKind> Kinds,
    IReadOnlyList<FilterValue> Filters,
    string? Sort = null,
    string? Cursor = null,
    int? Limit = null);
