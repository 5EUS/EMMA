using System.Text.Json;
using System.Text.Json.Serialization;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Source-generated JSON serialization context for PluginManifest and related types.
/// Required for NativeAOT compatibility where reflection-based serialization is disabled.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginManifestRuntime))]
[JsonSerializable(typeof(PluginManifestThumbnail))]
[JsonSerializable(typeof(PluginManifestCapabilities))]
[JsonSerializable(typeof(PluginManifestPermissions))]
[JsonSerializable(typeof(PluginManifestSignature))]
[JsonSerializable(typeof(PluginManifestSearchExperience))]
[JsonSerializable(typeof(PluginManifestSearchLanding))]
[JsonSerializable(typeof(PluginManifestSearchLandingSection))]
[JsonSerializable(typeof(PluginManifestSearchLandingAction))]
[JsonSerializable(typeof(PluginManifestSearchFilter))]
[JsonSerializable(typeof(PluginManifestSearchFilterRange))]
[JsonSerializable(typeof(PluginManifestSearchFilterOption))]
[JsonSerializable(typeof(PluginManifestSearchQueryOptions))]
[JsonSerializable(typeof(PluginManifestSearchQueryAddition))]
[JsonSerializable(typeof(List<PluginManifestSearchFilter>))]
[JsonSerializable(typeof(List<PluginManifestSearchFilterOption>))]
[JsonSerializable(typeof(List<PluginManifestSearchLandingSection>))]
[JsonSerializable(typeof(List<PluginManifestSearchLandingAction>))]
[JsonSerializable(typeof(List<PluginManifestSearchQueryAddition>))]
[JsonSerializable(typeof(List<PluginManifest>))]
public partial class PluginManifestJsonContext : JsonSerializerContext
{
}
