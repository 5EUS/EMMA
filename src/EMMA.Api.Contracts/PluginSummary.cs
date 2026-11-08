namespace EMMA.Api.Contracts;

/// <summary>One installed plugin.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Status"><c>enabled</c>, <c>disabled</c> or <c>quarantined</c>.</param>
/// <param name="Signer">Who vouched for it, or none for an unsigned development install.</param>
public sealed record PluginSummary(
    string Id,
    string Version,
    string Status,
    string? Signer);
