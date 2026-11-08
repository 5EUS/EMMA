namespace EMMA.WasmRuntime.Wire;

/// <summary>What a plugin says about itself.</summary>
/// <param name="Kinds">The kinds.</param>
/// <param name="Filters">The filters.</param>
/// <param name="Sorts">The sorts.</param>
/// <param name="Categories">The settings groups.</param>
/// <param name="Settings">The settings.</param>
internal sealed record WireInfo(
    IReadOnlyList<WireKind> Kinds,
    IReadOnlyList<WireFilter> Filters,
    IReadOnlyList<WireChoice> Sorts,
    IReadOnlyList<WireCategory> Categories,
    IReadOnlyList<WireSetting> Settings);
