using EMMA.Domain;
using EMMA.PluginHost.Manifest;
using EMMA.WasmRuntime;

namespace EMMA.PluginHost.Runtime;

/// <summary>
/// A plugin that passed every check and is open in the Wasm host. Whoever holds it disposes
/// <paramref name="Plugin"/> when the plugin is unloaded.
/// </summary>
/// <param name="Plugin">The open plugin.</param>
/// <param name="Manifest">The verified manifest.</param>
/// <param name="Info">What the plugin declared in <c>describe</c> when it was loaded.</param>
public sealed record LoadedPlugin(WasmPlugin Plugin, PluginManifest Manifest, PluginInfo Info);
