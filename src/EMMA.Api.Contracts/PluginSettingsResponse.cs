namespace EMMA.Api.Contracts;

/// <summary>The user's values for a plugin's settings.</summary>
/// <param name="Settings">One entry per declared setting, in declaration order. The declaration itself (labels, types, defaults) is in the plugin detail.</param>
public sealed record PluginSettingsResponse(
    IReadOnlyList<SettingStateDto> Settings);
