namespace EMMA.Domain;

/// <summary>A change a user asks for to one setting of one plugin.</summary>
/// <param name="Key">The setting key, as the plugin declared it.</param>
/// <param name="Values">The new values as text (one, except for a multi-choice setting). Empty clears the setting, so the plugin's default applies again.</param>
public sealed record SettingChange(string Key, IReadOnlyList<string> Values);
