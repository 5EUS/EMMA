namespace EMMA.WasmRuntime.Wire;

/// <summary>A request by ids, for <c>get</c> and <c>enrich</c>.</summary>
/// <param name="Ids">The ids.</param>
internal sealed record WireIdsRequest(IReadOnlyList<string> Ids);
