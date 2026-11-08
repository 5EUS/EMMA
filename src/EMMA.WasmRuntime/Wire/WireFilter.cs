using EMMA.Domain;

namespace EMMA.WasmRuntime.Wire;

/// <summary>A declared search filter.</summary>
/// <param name="Id">The id.</param>
/// <param name="Label">The label.</param>
/// <param name="FilterType">The type.</param>
/// <param name="Kinds">The kinds it applies to.</param>
/// <param name="Required">Whether it is required.</param>
/// <param name="Hidden">Whether it is hidden.</param>
/// <param name="Group">The group.</param>
/// <param name="Description">A description.</param>
/// <param name="Choices">The options.</param>
/// <param name="Range">The bounds.</param>
/// <param name="Lookup">The lookup behaviour.</param>
internal sealed record WireFilter(
    string Id,
    string Label,
    FilterType FilterType,
    IReadOnlyList<string> Kinds,
    bool Required,
    bool Hidden,
    string? Group,
    string? Description,
    IReadOnlyList<WireChoice> Choices,
    WireRange? Range,
    WireLookup? Lookup);
