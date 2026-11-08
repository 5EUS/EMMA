namespace EMMA.Api.Contracts;

/// <summary>One installed plugin and, when it can run, what it declares.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Status"><c>enabled</c>, <c>disabled</c> or <c>quarantined</c>.</param>
/// <param name="Signer">Who vouched for it.</param>
/// <param name="Info">The declaration; absent unless the plugin is enabled and answered.</param>
public sealed record PluginDetailResponse(
    string Id,
    string Version,
    string Status,
    string? Signer,
    PluginInfoDto? Info);
