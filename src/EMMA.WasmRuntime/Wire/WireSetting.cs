using EMMA.Domain;

namespace EMMA.WasmRuntime.Wire;

/// <summary>A declared setting.</summary>
/// <param name="Key">The key.</param>
/// <param name="Label">The label.</param>
/// <param name="Description">A description.</param>
/// <param name="Help">Help text.</param>
/// <param name="SettingType">The type.</param>
/// <param name="Category">The category id.</param>
/// <param name="Required">Whether it is required.</param>
/// <param name="DefaultValues">The defaults.</param>
/// <param name="Choices">The options.</param>
/// <param name="Placeholder">The placeholder.</param>
/// <param name="MaxLength">The longest value.</param>
/// <param name="VisibleWhen">The visibility condition.</param>
internal sealed record WireSetting(
    string Key,
    string Label,
    string? Description,
    string? Help,
    SettingType SettingType,
    string? Category,
    bool Required,
    IReadOnlyList<string> DefaultValues,
    IReadOnlyList<WireChoice> Choices,
    string? Placeholder,
    uint? MaxLength,
    WireCondition? VisibleWhen);
