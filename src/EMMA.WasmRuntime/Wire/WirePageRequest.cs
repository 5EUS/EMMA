namespace EMMA.WasmRuntime.Wire;

/// <summary>A cursor and page size.</summary>
/// <param name="Cursor">The cursor.</param>
/// <param name="Limit">The page size hint.</param>
internal sealed record WirePageRequest(string? Cursor, uint? Limit);
