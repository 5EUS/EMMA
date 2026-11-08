using System.Text;

using EMMA.Domain;
using EMMA.PluginHost.Manifest;

namespace EMMA.TestSupport;

/// <summary>Builders for plugin manifests used across the test suites.</summary>
public static class TestManifests
{
    /// <summary>Builds a complete, valid, unsigned manifest.</summary>
    /// <param name="id">The plugin id.</param>
    /// <param name="entrypointSha256">The entrypoint digest.</param>
    /// <returns>The manifest.</returns>
    public static PluginManifest Valid(string id = "com.example.test", string? entrypointSha256 = null) => new(
        PluginId.Create(id),
        "Test plugin",
        "1.0.0",
        "A plugin for tests",
        "Tester",
        [NodeKind.Create("series"), NodeKind.Create("series.chapter")],
        new ManifestIcon(1.5, "cover", 64, 64),
        new ManifestContract(
            ["emma:core/meta@1", "emma:core/catalog@1", "emma:core/assets@1"],
            ["emma:core/search-assist@1"]),
        "1.0.0",
        new ManifestEntrypoint("plugin.wasm", entrypointSha256 ?? new string('a', 64)),
        new ManifestPermissions(["api.example.com", "*.cdn.example.com"], true),
        new ManifestLimits(5000, 128),
        null);

    /// <summary>Renders a manifest as JSON text.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The JSON.</returns>
    public static string Json(PluginManifest manifest) => Encoding.UTF8.GetString(ManifestWriter.Write(manifest));
}
