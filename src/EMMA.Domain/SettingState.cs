namespace EMMA.Domain;

/// <summary>Where one declared setting stands: what the user set, and whether the plugin still lacks something it needs.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="IsSet">Whether the user has set it. When not, the plugin's default (if any) applies.</param>
/// <param name="Values">The values the user set, as text. Always empty for a secret.</param>
/// <param name="Missing">Whether the setting is required, unset, and has no default.</param>
public sealed record SettingState(string Key, bool IsSet, IReadOnlyList<string> Values, bool Missing);
