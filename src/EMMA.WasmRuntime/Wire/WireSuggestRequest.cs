namespace EMMA.WasmRuntime.Wire;

/// <summary>A request for search suggestions.</summary>
/// <param name="FilterId">The lookup filter being completed.</param>
/// <param name="Text">The partial text.</param>
/// <param name="Limit">The most suggestions.</param>
internal sealed record WireSuggestRequest(string FilterId, string Text, uint Limit);
