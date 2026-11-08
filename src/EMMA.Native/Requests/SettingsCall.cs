using EMMA.Api.Contracts;

namespace EMMA.Native.Requests;

/// <summary>Changes some of a plugin's settings.</summary>
/// <param name="Plugin">The plugin id.</param>
/// <param name="Changes">The changes; see <c>SetSettingsRequest</c>.</param>
internal sealed record SettingsCall(
    string? Plugin,
    IReadOnlyList<SettingChangeDto>? Changes);
