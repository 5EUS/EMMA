using System.Text.Json;

namespace EMMA.PluginHost.Plugins;

public sealed record PluginManifestPreferences
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<PluginManifestPreferenceCategory>? Categories { get; init; }
    public IReadOnlyList<PluginManifestPreferenceField>? Fields { get; init; }
}

public sealed record PluginManifestPreferenceCategory
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public sealed record PluginManifestPreferenceOption
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsDefault { get; init; }
}

public sealed record PluginManifestPreferenceSecretOptions
{
    public string? Protection { get; init; }
    public bool RevealInUi { get; init; }
    public bool AllowCopy { get; init; }
}

public sealed record PluginManifestPreferenceVisibilityRule
{
    public string Field { get; init; } = string.Empty;
    public JsonElement? EqualsValue { get; init; }
}

public sealed record PluginManifestPreferenceField
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Category { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public JsonElement? DefaultValue { get; init; }
    public string? Placeholder { get; init; }
    public string? HelpText { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public string? Pattern { get; init; }
    public bool RestartRequired { get; init; }
    public bool Sensitive { get; init; }
    public string? SyncPolicy { get; init; }
    public PluginManifestPreferenceVisibilityRule? VisibleWhen { get; init; }
    public IReadOnlyList<PluginManifestPreferenceOption>? Options { get; init; }
    public PluginManifestPreferenceSecretOptions? Secret { get; init; }
}