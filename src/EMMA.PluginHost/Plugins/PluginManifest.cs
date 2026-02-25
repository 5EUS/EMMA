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
    string? Author);

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
/// </summary>
public static class PluginManifestDefaults
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
