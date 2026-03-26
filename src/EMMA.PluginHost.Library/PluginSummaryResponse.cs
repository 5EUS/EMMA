using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Library;

/// <summary>
/// Response data for plugin listings.
/// Used for JSON serialization in NativeAOT scenarios.
/// </summary>
public sealed record PluginSummaryResponse(
    string Id,
    string Title,
    string Version,
    string Author,
    PluginManifestSearchExperience? SearchExperience = null);
