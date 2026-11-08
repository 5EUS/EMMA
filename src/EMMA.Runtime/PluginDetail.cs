using EMMA.Domain;

namespace EMMA.Runtime;

/// <summary>An installed plugin and, when it is enabled and answered, what it declares.</summary>
/// <param name="Plugin">The installed record.</param>
/// <param name="Info">The plugin's declaration, or <see langword="null"/> when it is not enabled.</param>
public sealed record PluginDetail(InstalledPlugin Plugin, PluginInfo? Info);
