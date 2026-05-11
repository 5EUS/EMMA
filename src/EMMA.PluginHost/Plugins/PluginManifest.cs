using System.Text.Json;

namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Describes a plugin and its runtime metadata.
/// </summary>
/// <param name="Id">The stable plugin identifier.</param>
/// <param name="Name">The human-readable plugin name.</param>
/// <param name="Version">The plugin version string.</param>
/// <param name="Protocol">The runtime protocol used to communicate with the plugin.</param>
/// <param name="Endpoint">The externally exposed plugin endpoint, when applicable.</param>
/// <param name="MediaTypes">The media types supported by the plugin.</param>
/// <param name="Capabilities">Declared plugin capability metadata.</param>
/// <param name="Permissions">Declared permission requirements for the plugin.</param>
/// <param name="Signature">Optional repository signature metadata.</param>
/// <param name="Description">An optional human-readable plugin description.</param>
/// <param name="Author">The plugin author or publisher.</param>
/// <param name="Runtime">Optional runtime compatibility metadata.</param>
/// <param name="Thumbnail">Optional thumbnail display metadata.</param>
/// <param name="SearchExperience">Optional search UI metadata exposed by the plugin.</param>
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
    PluginManifestThumbnail? Thumbnail = null,
    PluginManifestSearchExperience? SearchExperience = null);

/// <summary>
/// Describes thumbnail presentation metadata for a plugin.
/// </summary>
/// <param name="AspectRatio">The preferred thumbnail aspect ratio.</param>
/// <param name="Fit">The preferred image fit mode.</param>
/// <param name="Width">The preferred thumbnail width in pixels.</param>
/// <param name="Height">The preferred thumbnail height in pixels.</param>
public sealed record PluginManifestThumbnail(
    double? AspectRatio,
    string? Fit,
    int? Width,
    int? Height);

/// <summary>
/// Describes the search experience metadata exposed by a plugin.
/// </summary>
/// <param name="Landing">Landing page metadata for the search UI.</param>
/// <param name="Filters">Available search filters.</param>
/// <param name="Query">Search query configuration.</param>
/// <param name="Capabilities">Search capability flags exposed to the client.</param>
public sealed record PluginManifestSearchExperience(
    PluginManifestSearchLanding? Landing,
    IReadOnlyList<PluginManifestSearchFilter>? Filters,
    PluginManifestSearchQueryOptions? Query,
    IReadOnlyList<string>? Capabilities);

/// <summary>
/// Describes the landing content shown before a user performs a search.
/// </summary>
/// <param name="Template">The landing layout template identifier.</param>
/// <param name="Title">The landing title text.</param>
/// <param name="Subtitle">The landing subtitle text.</param>
/// <param name="Sections">The landing sections to render.</param>
public sealed record PluginManifestSearchLanding(
    string? Template,
    string? Title,
    string? Subtitle,
    IReadOnlyList<PluginManifestSearchLandingSection>? Sections);

/// <summary>
/// Describes a section rendered on the plugin search landing page.
/// </summary>
/// <param name="Id">The section identifier.</param>
/// <param name="Type">The section type.</param>
/// <param name="Title">The section title.</param>
/// <param name="Description">The section description.</param>
/// <param name="MediaTypes">The media types associated with the section.</param>
/// <param name="Actions">The actions exposed by the section.</param>
/// <param name="DataSource">The optional data source identifier for the section.</param>
public sealed record PluginManifestSearchLandingSection(
    string Id,
    string Type,
    string? Title,
    string? Description,
    IReadOnlyList<string>? MediaTypes,
    IReadOnlyList<PluginManifestSearchLandingAction>? Actions,
    string? DataSource);

/// <summary>
/// Describes a clickable action exposed on a search landing section.
/// </summary>
/// <param name="Id">The action identifier.</param>
/// <param name="Label">The action label.</param>
/// <param name="Type">The action type.</param>
/// <param name="Value">The action value or payload.</param>
public sealed record PluginManifestSearchLandingAction(
    string Id,
    string Label,
    string Type,
    string? Value);

/// <summary>
/// Describes a search filter that can be applied to plugin queries.
/// </summary>
/// <param name="Id">The filter identifier.</param>
/// <param name="Label">The filter label.</param>
/// <param name="Type">The filter type.</param>
/// <param name="AppliesToMediaTypes">The media types the filter applies to.</param>
/// <param name="IsRequired">Whether the filter must be supplied.</param>
/// <param name="IsHidden">Whether the filter should be hidden from the UI.</param>
/// <param name="IsCustom">Whether the filter is provided dynamically by the plugin.</param>
/// <param name="Group">The UI group name for the filter.</param>
/// <param name="Description">A description of the filter.</param>
/// <param name="DefaultValue">The default single value.</param>
/// <param name="DefaultValues">The default multi-value selection.</param>
/// <param name="Range">The numeric range configuration, when applicable.</param>
/// <param name="Options">The discrete filter options, when applicable.</param>
/// <param name="CustomProperties">Additional plugin-defined filter metadata.</param>
public sealed record PluginManifestSearchFilter(
    string Id,
    string Label,
    string Type,
    IReadOnlyList<string>? AppliesToMediaTypes,
    bool IsRequired = false,
    bool IsHidden = false,
    bool IsCustom = false,
    string? Group = null,
    string? Description = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? DefaultValues = null,
    PluginManifestSearchFilterRange? Range = null,
    IReadOnlyList<PluginManifestSearchFilterOption>? Options = null,
    IReadOnlyDictionary<string, string>? CustomProperties = null);

/// <summary>
/// Describes the range metadata for a numeric search filter.
/// </summary>
/// <param name="Min">The minimum allowed value.</param>
/// <param name="Max">The maximum allowed value.</param>
/// <param name="Step">The recommended step size.</param>
/// <param name="Unit">The unit label for displayed values.</param>
public sealed record PluginManifestSearchFilterRange(
    double? Min,
    double? Max,
    double? Step,
    string? Unit);

/// <summary>
/// Describes a selectable option for a search filter or query addition.
/// </summary>
/// <param name="Value">The submitted option value.</param>
/// <param name="Label">The user-facing option label.</param>
/// <param name="MediaTypes">The media types the option applies to.</param>
/// <param name="IsDefault">Whether the option is selected by default.</param>
public sealed record PluginManifestSearchFilterOption(
    string Value,
    string Label,
    IReadOnlyList<string>? MediaTypes = null,
    bool IsDefault = false);

/// <summary>
/// Describes how free-text query input is configured for a plugin search surface.
/// </summary>
/// <param name="Additions">The structured query additions supported by the plugin.</param>
/// <param name="AllowFreeText">Whether plain free-text query entry is allowed.</param>
/// <param name="MinLength">The minimum allowed query length.</param>
/// <param name="MaxLength">The maximum allowed query length.</param>
public sealed record PluginManifestSearchQueryOptions(
    IReadOnlyList<PluginManifestSearchQueryAddition>? Additions,
    bool AllowFreeText = true,
    int? MinLength = null,
    int? MaxLength = null);

/// <summary>
/// Describes a structured query field that can be combined with free-text search.
/// </summary>
/// <param name="Id">The addition identifier.</param>
/// <param name="Label">The addition label.</param>
/// <param name="Type">The addition type.</param>
/// <param name="Prefix">An optional prefix inserted before the value.</param>
/// <param name="Suffix">An optional suffix inserted after the value.</param>
/// <param name="Placeholder">Placeholder text shown in the UI.</param>
/// <param name="DefaultValue">The default value.</param>
/// <param name="Options">The selectable options, when applicable.</param>
/// <param name="AppliesToMediaTypes">The media types the addition applies to.</param>
/// <param name="IsRequired">Whether the addition is required.</param>
public sealed record PluginManifestSearchQueryAddition(
    string Id,
    string Label,
    string Type,
    string? Prefix = null,
    string? Suffix = null,
    string? Placeholder = null,
    string? DefaultValue = null,
    IReadOnlyList<PluginManifestSearchFilterOption>? Options = null,
    IReadOnlyList<string>? AppliesToMediaTypes = null,
    bool IsRequired = false);

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
    string Value,
    string? KeyId = null,
    string? RepositoryId = null,
    string? IssuedAtUtc = null,
    string? ExpiresAtUtc = null,
    string? ManifestDigestSha256 = null,
    string? PayloadDigestSha256 = null);

/// <summary>
/// Shared JSON serializer defaults for manifest parsing.
/// Uses source-generated context for NativeAOT compatibility.
/// </summary>
public static class PluginManifestDefaults
{
    /// <summary>
    /// Gets the shared serializer options used for plugin manifest JSON parsing.
    /// </summary>
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
