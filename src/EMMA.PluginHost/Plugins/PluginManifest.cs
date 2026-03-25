using System.Text.Json;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Describes a plugin and its runtime metadata.
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Protocol,
    string? Endpoint,
    IReadOnlyList<string>? MediaTypes,
    PluginManifestCapabilities? Capabilities,
    PluginManifestPermissions? Permissions,
    PluginManifestSignature? Signature,
    string? Description,
    string? Author,
    PluginManifestRuntime? Runtime = null,
    PluginManifestThumbnail? Thumbnail = null);

public sealed record PluginManifestThumbnail(
    double? AspectRatio,
    string? Fit,
    int? Width,
    int? Height);

/// <summary>
/// Declares runtime routing metadata for a plugin.
/// </summary>
public sealed record PluginManifestRuntime(
    string? MinHostVersion);

/// <summary>
/// Declared plugin resource and capability hints.
/// </summary>
public sealed record PluginManifestCapabilities(
    IReadOnlyList<string>? Network,
    IReadOnlyList<string>? FileSystem,
    bool Cache,
    int CpuBudgetMs,
    int MemoryMb);

/// <summary>
/// Allowed domains and paths for plugin access.
/// </summary>
public sealed record PluginManifestPermissions(
    IReadOnlyList<string>? Domains,
    IReadOnlyList<string>? Paths);

/// <summary>
/// Optional signature metadata for plugin manifests.
/// </summary>
public sealed record PluginManifestSignature(
    string Algorithm,
    string Value);

/// <summary>
/// Shared JSON serializer defaults for manifest parsing.
/// Uses source-generated context for NativeAOT compatibility.
/// </summary>
public static class PluginManifestDefaults
{
    public static readonly JsonSerializerOptions JsonOptions = GetOptions();

    private static JsonSerializerOptions GetOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
#pragma warning disable SYSLIB0049
        options.AddContext<PluginManifestJsonContext>();
#pragma warning restore SYSLIB0049
        return options;
    }
}
