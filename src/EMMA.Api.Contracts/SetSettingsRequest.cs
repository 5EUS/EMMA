namespace EMMA.Api.Contracts;

/// <summary>Changes some of a plugin's settings, all or none. A setting left out keeps its value.</summary>
/// <param name="Changes">The changes, each setting at most once.</param>
public sealed record SetSettingsRequest(
    IReadOnlyList<SettingChangeDto>? Changes);
