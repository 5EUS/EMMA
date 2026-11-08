namespace EMMA.Domain;

/// <summary>A validated change handed to the settings store, which also needs to know whether to encrypt it.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="Values">The values as text. Empty removes the setting.</param>
/// <param name="IsSecret">Whether the values are stored encrypted.</param>
public sealed record SettingWrite(string Key, IReadOnlyList<string> Values, bool IsSecret);
