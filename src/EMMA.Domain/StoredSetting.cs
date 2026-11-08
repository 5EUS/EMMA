namespace EMMA.Domain;

/// <summary>What the store holds for one setting the user has set.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="IsSecret">Whether the value is a secret.</param>
/// <param name="Values">The values as text. Always empty for a secret: a secret is never read back, only known to be set.</param>
public sealed record StoredSetting(string Key, bool IsSecret, IReadOnlyList<string> Values);
