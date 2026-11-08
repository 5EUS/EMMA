namespace EMMA.PluginHost.Manifest;

/// <summary>How the plugin's own icon is presented.</summary>
/// <param name="AspectRatio">The preferred aspect ratio, if any.</param>
/// <param name="Fit">The preferred fit: <c>cover</c>, <c>contain</c> or <c>fill</c>.</param>
/// <param name="Width">The preferred width in pixels.</param>
/// <param name="Height">The preferred height in pixels.</param>
public sealed record ManifestIcon(double? AspectRatio, string? Fit, int? Width, int? Height);
