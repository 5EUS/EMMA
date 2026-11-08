using EMMA.Domain;
using EMMA.PluginHost.Instances;
using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Runtime;

/// <summary>
/// A plugin that passed every check and is running. Whoever holds it disposes
/// <paramref name="Instance"/> when the plugin is unloaded.
/// </summary>
/// <param name="Instance">The running plugin.</param>
/// <param name="Manifest">The verified manifest.</param>
/// <param name="Info">What the plugin declared in <c>describe</c> when it was loaded.</param>
public sealed record LoadedPlugin(IPluginInstance Instance, PluginManifest Manifest, PluginInfo Info);
