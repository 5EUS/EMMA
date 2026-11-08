namespace EMMA.Api.Contracts;

/// <summary>Where one of a plugin's declared settings stands.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="IsSet">Whether the user has set it. When not, the plugin's default (from the declaration) applies.</param>
/// <param name="Values">The values the user set, as text. Always empty for a secret, which is never shown back.</param>
/// <param name="Missing">Whether the setting is required, unset, and has no default, so the plugin cannot work yet.</param>
public sealed record SettingStateDto(
    string Key,
    bool IsSet,
    IReadOnlyList<string> Values,
    bool Missing);
