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
    string BuildType,
    double? ThumbnailAspectRatio = null,
    string? ThumbnailFit = null,
    int? ThumbnailWidth = null,
    int? ThumbnailHeight = null,
    PluginManifestSearchExperience? SearchExperience = null);
