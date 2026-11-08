namespace EMMA.Domain;

/// <summary>Declares a user setting a plugin reads through <c>host.setting</c>.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="Label">The display label.</param>
/// <param name="Description">An optional description.</param>
/// <param name="Secret">Secret values are stored encrypted and never logged or exported.</param>
/// <param name="Required">Whether the plugin cannot work without it.</param>
/// <param name="DefaultValue">The default, if any.</param>
public sealed record SettingSpec(
    string Key,
    string Label,
    string? Description,
    bool Secret,
    bool Required,
    string? DefaultValue);
