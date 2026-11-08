namespace EMMA.Api.Contracts;

/// <summary>A search request.</summary>
/// <param name="Text">The search text; may be empty.</param>
/// <param name="Kinds">Restrict results to these kinds.</param>
/// <param name="Filters">The chosen filter values.</param>
/// <param name="Sort">One of the plugin's sorts.</param>
/// <param name="Cursor">The previous page's next cursor.</param>
/// <param name="Limit">A hint for the page size.</param>
internal sealed record SearchRequest(
    string? Text,
    IReadOnlyList<string>? Kinds,
    IReadOnlyList<FilterValueDto>? Filters,
    string? Sort,
    string? Cursor,
    int? Limit);
