namespace EMMA.Api.Contracts;

/// <summary>A user setting the plugin declares.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="Label">The display label.</param>
/// <param name="Description">An optional description.</param>
/// <param name="Help">A longer explanation.</param>
/// <param name="Type"><c>text</c>, <c>secret</c>, <c>boolean</c>, <c>number</c>, <c>duration</c>, <c>choice</c> or <c>multi_choice</c>.</param>
/// <param name="Category">The category id.</param>
/// <param name="Required">Whether the plugin cannot work without a value.</param>
/// <param name="DefaultValues">Defaults as text.</param>
/// <param name="Choices">Options for choice settings.</param>
/// <param name="Placeholder">Hint text for an empty field.</param>
/// <param name="MaxLength">The longest accepted value.</param>
/// <param name="VisibleWhen">When to show the setting.</param>
internal sealed record SettingDto(
    string Key,
    string Label,
    string? Description,
    string? Help,
    string Type,
    string? Category,
    bool Required,
    IReadOnlyList<string> DefaultValues,
    IReadOnlyList<ChoiceDto> Choices,
    string? Placeholder,
    int? MaxLength,
    ConditionDto? VisibleWhen);
