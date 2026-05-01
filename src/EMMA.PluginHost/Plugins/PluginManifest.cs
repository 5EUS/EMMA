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
    PluginManifestThumbnail? Thumbnail = null,
    PluginManifestSearchExperience? SearchExperience = null);

public sealed record PluginManifestThumbnail(
    double? AspectRatio,
    string? Fit,
    int? Width,
    int? Height);

public sealed record PluginManifestSearchExperience(
    PluginManifestSearchLanding? Landing,
    IReadOnlyList<PluginManifestSearchFilter>? Filters,
    PluginManifestSearchQueryOptions? Query,
    IReadOnlyList<string>? Capabilities);

public sealed record PluginManifestSearchLanding(
    string? Template,
    string? Title,
    string? Subtitle,
    IReadOnlyList<PluginManifestSearchLandingSection>? Sections);

public sealed record PluginManifestSearchLandingSection(
    string Id,
    string Type,
    string? Title,
    string? Description,
    IReadOnlyList<string>? MediaTypes,
    IReadOnlyList<PluginManifestSearchLandingAction>? Actions,
    string? DataSource);

public sealed record PluginManifestSearchLandingAction(
    string Id,
    string Label,
    string Type,
    string? Value);

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

public sealed record PluginManifestSearchFilterRange(
    double? Min,
    double? Max,
    double? Step,
    string? Unit);

public sealed record PluginManifestSearchFilterOption(
    string Value,
    string Label,
    IReadOnlyList<string>? MediaTypes = null,
    bool IsDefault = false);

public sealed record PluginManifestSearchQueryOptions(
    IReadOnlyList<PluginManifestSearchQueryAddition>? Additions,
    bool AllowFreeText = true,
    int? MinLength = null,
    int? MaxLength = null);

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
