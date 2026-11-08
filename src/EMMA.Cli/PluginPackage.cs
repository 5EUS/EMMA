using EMMA.PluginHost.Manifest;

namespace EMMA.Cli;

/// <summary>A plugin package, in memory.</summary>
/// <param name="Manifest">The manifest as packaged, signed if a key was given.</param>
/// <param name="Archive">The zip bytes.</param>
internal sealed record PluginPackage(PluginManifest Manifest, byte[] Archive);
