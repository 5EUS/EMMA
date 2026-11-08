namespace EMMA.WasmRuntime.Wire;

/// <summary>A search request.</summary>
/// <param name="Text">The text.</param>
/// <param name="Kinds">The kinds to restrict to.</param>
/// <param name="Filters">The filters.</param>
/// <param name="Sort">The sort.</param>
/// <param name="Cursor">The cursor.</param>
/// <param name="Limit">The page size hint.</param>
internal sealed record WireQuery(
    string Text,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<WireFilterValue> Filters,
    string? Sort,
    string? Cursor,
    uint? Limit);
