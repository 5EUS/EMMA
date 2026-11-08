namespace EMMA.Api.Contracts;

/// <summary>The installed plugins.</summary>
/// <param name="Plugins">The plugins, by id.</param>
public sealed record PluginListResponse(
    IReadOnlyList<PluginSummary> Plugins);
