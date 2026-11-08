namespace EMMA.PluginHost.Manifest;

/// <summary>The WIT interfaces the component implements, by interface id such as <c>emma:core/catalog@1</c>.</summary>
/// <param name="Required">Interfaces the plugin cannot run without.</param>
/// <param name="Optional">Extension interfaces the plugin also implements.</param>
public sealed record ManifestContract(IReadOnlyList<string> Required, IReadOnlyList<string> Optional);
