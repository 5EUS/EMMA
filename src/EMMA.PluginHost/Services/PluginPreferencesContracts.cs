using System.Text.Json;

namespace EMMA.PluginHost.Services;

public sealed record PluginPreferenceCategoryResponse(
    string Id,
    string Label,
    string? Description);

public sealed record PluginPreferenceOptionResponse(
    string Value,
    string Label,
    string? Description,
    bool IsDefault = false);

public sealed record PluginPreferenceSecretPolicyResponse(
    string RequestedProtection,
    bool RevealInUi,
    bool AllowCopy,
    bool ProtectionSupported);

public sealed record PluginPreferenceValueSnapshotResponse(
    string FieldKey,
    string FieldType,
    string State,
    bool HasStoredValue,
    bool UsesDefaultValue,
    bool IsSecret,
    string? ProtectionLevel,
    string? ValidationError,
    JsonElement? Value = null);

public sealed record PluginPreferenceFieldResponse(
    string Key,
    string Label,
    string Type,
    string? Category,
    string? Description,
    bool Required,
    string? Placeholder,
    string? HelpText,
    int? MinLength,
    int? MaxLength,
    double? Min,
    double? Max,
    double? Step,
    string? Pattern,
    bool RestartRequired,
    bool Sensitive,
    string SyncPolicy,
    bool IsVisible,
    JsonElement? DefaultValue,
    IReadOnlyList<PluginPreferenceOptionResponse> Options,
    PluginPreferenceSecretPolicyResponse? Secret,
    PluginPreferenceValueSnapshotResponse ValueState);

public sealed record PluginPreferenceSummaryResponse(
    bool HasPreferences,
    int FieldCount,
    int RequiredUnsetCount,
    int SecretFieldCount,
    int InvalidFieldCount);

public sealed record PluginPreferenceSchemaResponse(
    string PluginId,
    string PluginName,
    string PluginVersion,
    int SchemaVersion,
    IReadOnlyList<string> SupportedProtectionLevels,
    IReadOnlyList<PluginPreferenceCategoryResponse> Categories,
    IReadOnlyList<PluginPreferenceFieldResponse> Fields,
    IReadOnlyList<string> Issues,
    PluginPreferenceSummaryResponse Summary);

public sealed record PluginPreferenceMutationRequest(
    JsonElement? Value,
    string? ProtectionLevel = null);

public sealed record PluginPreferenceMutationResult(
    bool Success,
    PluginPreferenceValueSnapshotResponse? ValueState,
    IReadOnlyList<string> Warnings,
    string? Error = null);

public sealed record PluginPreferenceValidationIssueResponse(
    string FieldKey,
    string Message);

public sealed record PluginPreferenceValidationResponse(
    bool IsValid,
    IReadOnlyList<PluginPreferenceValidationIssueResponse> Issues);