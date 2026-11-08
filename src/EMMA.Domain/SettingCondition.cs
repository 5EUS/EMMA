namespace EMMA.Domain;

/// <summary>Shows a setting only while another setting has a given value (for a multi-choice, while it is among the chosen ones).</summary>
/// <param name="Key">The key of another setting.</param>
/// <param name="Value">The value that makes the setting visible.</param>
public sealed record SettingCondition(string Key, string Value);
