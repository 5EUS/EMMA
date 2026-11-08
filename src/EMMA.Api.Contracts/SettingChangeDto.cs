namespace EMMA.Api.Contracts;

/// <summary>A change to one setting.</summary>
/// <param name="Key">The setting key.</param>
/// <param name="Values">The new values as text: one, except for a multi-choice setting. An empty list clears the setting so its default applies again.</param>
public sealed record SettingChangeDto(
    string? Key,
    IReadOnlyList<string>? Values);
