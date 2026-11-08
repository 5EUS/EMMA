namespace EMMA.Api.Contracts;

/// <summary>Changes what the app remembers under a scope, all or none.</summary>
/// <param name="Changes">The changes, each key at most once.</param>
public sealed record SetAppSettingsRequest(
    IReadOnlyList<AppSettingChangeDto>? Changes);
