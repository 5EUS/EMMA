namespace EMMA.Domain;

/// <summary>Declares a user setting a plugin reads through <c>host.setting</c>.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="Label">The display label.</param>
/// <param name="Description">An optional description.</param>
/// <param name="Help">A longer explanation shown beside the field.</param>
/// <param name="Type">How it is rendered, validated and read.</param>
/// <param name="Category">The id of the category it belongs to.</param>
/// <param name="Required">Whether the plugin cannot work without a value.</param>
/// <param name="DefaultValues">Defaults as text; at most one unless the type is multi-choice. Never set for a secret.</param>
/// <param name="Choices">The options for choice and multi-choice settings.</param>
/// <param name="Placeholder">Hint text for an empty field.</param>
/// <param name="MaxLength">The longest accepted text or secret value.</param>
/// <param name="VisibleWhen">When to show the setting.</param>
public sealed record SettingSpec(
    string Key,
    string Label,
    string? Description,
    string? Help,
    SettingType Type,
    string? Category,
    bool Required,
    IReadOnlyList<string> DefaultValues,
    IReadOnlyList<Choice> Choices,
    string? Placeholder,
    int? MaxLength,
    SettingCondition? VisibleWhen);
