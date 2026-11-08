using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Instances;

/// <summary>Everything a transport needs to start one verified plugin.</summary>
/// <param name="Manifest">The verified manifest: limits and permissions come from here.</param>
/// <param name="EntrypointPath">The entrypoint file, already hashed against the manifest.</param>
/// <param name="Settings">The user's settings as text values by key, secrets already decrypted.</param>
public sealed record PluginLaunch(
    PluginManifest Manifest,
    string EntrypointPath,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Settings);
