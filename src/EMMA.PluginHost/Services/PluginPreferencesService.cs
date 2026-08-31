using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using EMMA.Application.Ports;
using EMMA.PluginHost.Plugins;

namespace EMMA.PluginHost.Services;

public sealed partial class PluginPreferencesService(
    PluginRegistry registry,
    IPluginPreferenceStore store,
    EMMA.Storage.ProtectedPreferenceCipher cipher)
{
    private const int MaxFieldCount = 128;
    private const int MaxCategoryCount = 16;
    private const int MaxOptionsCount = 100;
    private const int MaxKeyLength = 128;
    private const int MaxDefaultStringLength = 4096;
    private const int MaxStoredJsonLength = 32768;
    private static readonly string[] SupportedProtectionLevels = ["protected", "session"];
    private static readonly HashSet<string> FieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "boolean", "int", "double", "string", "text", "enum", "multienum", "url", "duration", "json", "secret"
    };
    private static readonly ConcurrentDictionary<string, string> SessionSecrets = new(StringComparer.OrdinalIgnoreCase);

    private readonly PluginRegistry _registry = registry;
    private readonly IPluginPreferenceStore _store = store;
    private readonly EMMA.Storage.ProtectedPreferenceCipher _cipher = cipher;

    public async Task<PluginPreferenceSchemaResponse?> GetSchemaAsync(string pluginId, CancellationToken cancellationToken)
    {
        var manifest = GetManifest(pluginId);
        if (manifest is null)
        {
            return null;
        }

        return await BuildSchemaAsync(manifest, cancellationToken);
    }

    public async Task<PluginPreferenceSummaryResponse> GetSummaryAsync(string pluginId, CancellationToken cancellationToken)
    {
        var manifest = GetManifest(pluginId);
        if (manifest?.Preferences?.Fields is not { Count: > 0 })
        {
            return new PluginPreferenceSummaryResponse(false, 0, 0, 0, 0);
        }

        var schema = await BuildSchemaAsync(manifest, cancellationToken);
        return schema.Summary;
    }

    public async Task<PluginPreferenceMutationResult> SetValueAsync(
        string pluginId,
        string fieldKey,
        PluginPreferenceMutationRequest request,
        CancellationToken cancellationToken)
    {
        var manifest = GetManifest(pluginId);
        if (manifest is null)
        {
            return new PluginPreferenceMutationResult(false, null, [], "Plugin was not found.");
        }

        var schemaData = await BuildSchemaDataAsync(manifest, cancellationToken);
        var field = schemaData.Fields.FirstOrDefault(candidate => string.Equals(candidate.Key, fieldKey, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return new PluginPreferenceMutationResult(false, null, [], $"Preference field '{fieldKey}' was not found.");
        }

        if (request.Value is null || request.Value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new PluginPreferenceMutationResult(false, null, [], "A value is required.");
        }

        var warnings = new List<string>();
        var validation = ValidateValue(field, request.Value.Value, allowDefaultValue: false);
        if (!validation.Success)
        {
            return new PluginPreferenceMutationResult(false, null, warnings, validation.Error);
        }

        SessionSecrets.TryRemove(GetSessionSecretKey(pluginId, field.Key), out _);

        if (field.IsSecret)
        {
            var protectionLevel = NormalizeProtectionLevel(request.ProtectionLevel) ?? field.RequestedProtectionLevel;
            if (!IsProtectionSupported(protectionLevel))
            {
                return new PluginPreferenceMutationResult(false, null, warnings, $"Protection level '{protectionLevel}' is not supported in this build.");
            }

            var secretValue = validation.NormalizedElement!.Value.GetString() ?? string.Empty;
            if (string.Equals(protectionLevel, "session", StringComparison.OrdinalIgnoreCase))
            {
                SessionSecrets[GetSessionSecretKey(pluginId, field.Key)] = secretValue;
                await _store.DeleteAsync(pluginId, field.Key, cancellationToken);
            }
            else
            {
                var encrypted = _cipher.Encrypt(secretValue);
                await _store.UpsertAsync(
                    new PluginPreferenceStoreRecord(
                        pluginId,
                        field.Key,
                        field.Type,
                        protectionLevel,
                        true,
                        null,
                        encrypted,
                        "set",
                        schemaData.SchemaVersion,
                        null,
                        DateTimeOffset.UtcNow.ToString("O")),
                    cancellationToken);
            }
        }
        else
        {
            var serialized = validation.NormalizedElement!.Value.GetRawText();
            if (serialized.Length > MaxStoredJsonLength)
            {
                return new PluginPreferenceMutationResult(false, null, warnings, $"Stored value exceeds the {MaxStoredJsonLength}-byte limit.");
            }

            await _store.UpsertAsync(
                new PluginPreferenceStoreRecord(
                    pluginId,
                    field.Key,
                    field.Type,
                    "plain",
                    false,
                    serialized,
                    null,
                    "set",
                    schemaData.SchemaVersion,
                    null,
                    DateTimeOffset.UtcNow.ToString("O")),
                cancellationToken);
        }

        var refreshed = await BuildSchemaAsync(manifest, cancellationToken);
        var snapshot = refreshed.Fields.First(candidate => string.Equals(candidate.Key, field.Key, StringComparison.OrdinalIgnoreCase)).ValueState;
        return new PluginPreferenceMutationResult(true, snapshot, warnings);
    }

    public async Task<PluginPreferenceMutationResult> ClearValueAsync(
        string pluginId,
        string fieldKey,
        CancellationToken cancellationToken)
    {
        var manifest = GetManifest(pluginId);
        if (manifest is null)
        {
            return new PluginPreferenceMutationResult(false, null, [], "Plugin was not found.");
        }

        SessionSecrets.TryRemove(GetSessionSecretKey(pluginId, fieldKey), out _);
        await _store.DeleteAsync(pluginId, fieldKey, cancellationToken);
        var refreshed = await BuildSchemaAsync(manifest, cancellationToken);
        var snapshot = refreshed.Fields.FirstOrDefault(candidate => string.Equals(candidate.Key, fieldKey, StringComparison.OrdinalIgnoreCase))?.ValueState;
        return new PluginPreferenceMutationResult(true, snapshot, []);
    }

    public async Task<PluginPreferenceValidationResponse> ValidateAsync(string pluginId, CancellationToken cancellationToken)
    {
        var manifest = GetManifest(pluginId);
        if (manifest is null)
        {
            return new PluginPreferenceValidationResponse(false, [new PluginPreferenceValidationIssueResponse(pluginId, "Plugin was not found.")]);
        }

        var schema = await BuildSchemaAsync(manifest, cancellationToken);
        var issues = new List<PluginPreferenceValidationIssueResponse>();
        foreach (var issue in schema.Issues)
        {
            issues.Add(new PluginPreferenceValidationIssueResponse("$schema", issue));
        }

        foreach (var field in schema.Fields)
        {
            if (field.Required && field.ValueState.State != "set")
            {
                issues.Add(new PluginPreferenceValidationIssueResponse(field.Key, "A value is required."));
            }

            if (!string.IsNullOrWhiteSpace(field.ValueState.ValidationError))
            {
                issues.Add(new PluginPreferenceValidationIssueResponse(field.Key, field.ValueState.ValidationError!));
            }

            if (field.Secret is { ProtectionSupported: false })
            {
                issues.Add(new PluginPreferenceValidationIssueResponse(field.Key, $"Requested protection '{field.Secret.RequestedProtection}' is not supported in this build."));
            }
        }

        return new PluginPreferenceValidationResponse(issues.Count == 0, issues);
    }

    private async Task<PluginPreferenceSchemaResponse> BuildSchemaAsync(PluginManifest manifest, CancellationToken cancellationToken)
    {
        var data = await BuildSchemaDataAsync(manifest, cancellationToken);
        var fieldsByKey = data.Fields.ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);
        var fields = data.Fields.Select(field => BuildFieldResponse(field, fieldsByKey, data.StoredValues, data.SchemaVersion)).ToList();
        var summary = new PluginPreferenceSummaryResponse(
            fields.Count > 0,
            fields.Count,
            fields.Count(field => field.Required && field.ValueState.State != "set"),
            fields.Count(field => field.Secret is not null),
            fields.Count(field => string.Equals(field.ValueState.State, "invalid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(field.ValueState.State, "unavailable", StringComparison.OrdinalIgnoreCase)));

        return new PluginPreferenceSchemaResponse(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            data.SchemaVersion,
            SupportedProtectionLevels,
            data.Categories,
            fields,
            data.Issues,
            summary);
    }

    private async Task<SchemaData> BuildSchemaDataAsync(PluginManifest manifest, CancellationToken cancellationToken)
    {
        var preferences = manifest.Preferences;
        var issues = new List<string>();
        var categories = NormalizeCategories(preferences?.Categories, issues);
        var fields = NormalizeFields(manifest.Id, preferences?.Fields, categories, issues);
        var storedValues = (await _store.ListByPluginAsync(manifest.Id, cancellationToken))
            .ToDictionary(item => item.FieldKey, StringComparer.OrdinalIgnoreCase);
        return new SchemaData(preferences?.Version ?? 1, categories, fields, storedValues, issues);
    }

    private PluginManifest? GetManifest(string pluginId)
    {
        return _registry.GetSnapshot()
            .Select(item => item.Manifest)
            .FirstOrDefault(manifest => string.Equals(manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<PluginPreferenceCategoryResponse> NormalizeCategories(
        IReadOnlyList<PluginManifestPreferenceCategory>? categories,
        List<string> issues)
    {
        if (categories is null || categories.Count == 0)
        {
            return [];
        }

        if (categories.Count > MaxCategoryCount)
        {
            issues.Add($"Preference category count exceeds the supported limit of {MaxCategoryCount}.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PluginPreferenceCategoryResponse>();
        foreach (var category in categories.Take(MaxCategoryCount))
        {
            var id = NormalizeKey(category.Id);
            if (string.IsNullOrWhiteSpace(id))
            {
                issues.Add("Preference category id is required.");
                continue;
            }

            if (!seen.Add(id))
            {
                issues.Add($"Preference category '{id}' is duplicated.");
                continue;
            }

            var label = category.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label))
            {
                label = id;
            }

            results.Add(new PluginPreferenceCategoryResponse(id, label, category.Description?.Trim()));
        }

        return results;
    }

    private IReadOnlyList<NormalizedField> NormalizeFields(
        string pluginId,
        IReadOnlyList<PluginManifestPreferenceField>? fields,
        IReadOnlyList<PluginPreferenceCategoryResponse> categories,
        List<string> issues)
    {
        if (fields is null || fields.Count == 0)
        {
            return [];
        }

        if (fields.Count > MaxFieldCount)
        {
            issues.Add($"Preference field count exceeds the supported limit of {MaxFieldCount}.");
        }

        var categoryIds = categories.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<NormalizedField>();
        foreach (var field in fields.Take(MaxFieldCount))
        {
            var key = NormalizeKey(field.Key);
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyLength || !PreferenceKeyRegex().IsMatch(key))
            {
                issues.Add($"Preference field key '{field.Key}' is invalid.");
                continue;
            }

            if (!seen.Add(key))
            {
                issues.Add($"Preference field '{key}' is duplicated.");
                continue;
            }

            var type = (field.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (!FieldTypes.Contains(type))
            {
                issues.Add($"Preference field '{key}' uses unsupported type '{field.Type}'.");
                continue;
            }

            var category = NormalizeKey(field.Category);
            if (!string.IsNullOrWhiteSpace(category) && !categoryIds.Contains(category))
            {
                issues.Add($"Preference field '{key}' references unknown category '{field.Category}'.");
                category = null;
            }

            var options = NormalizeOptions(key, field.Options, type, issues);
            var secretOptions = NormalizeSecretOptions(type, field.Secret);
            if (type == "secret" && field.DefaultValue is not null && field.DefaultValue.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                issues.Add($"Preference field '{key}' cannot declare a default secret value.");
            }

            if (field.DefaultValue is { } defaultValue
                && defaultValue.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                var defaultValidation = ValidateValue(
                    new NormalizedField(
                        pluginId,
                        key,
                        field.Label?.Trim() ?? key,
                        type,
                        category,
                        field.Description?.Trim(),
                        field.Required,
                        field.Placeholder?.Trim(),
                        field.HelpText?.Trim(),
                        field.MinLength,
                        field.MaxLength,
                        field.Min,
                        field.Max,
                        field.Step,
                        field.Pattern?.Trim(),
                        field.RestartRequired,
                        field.Sensitive,
                        string.IsNullOrWhiteSpace(field.SyncPolicy) ? "device-local" : field.SyncPolicy.Trim().ToLowerInvariant(),
                        defaultValue.Clone(),
                        field.VisibleWhen,
                        options,
                        secretOptions),
                    defaultValue,
                    allowDefaultValue: true);
                if (!defaultValidation.Success)
                {
                    issues.Add($"Preference field '{key}' has an invalid default value: {defaultValidation.Error}");
                }
            }

            results.Add(
                new NormalizedField(
                    pluginId,
                    key,
                    field.Label?.Trim() ?? key,
                    type,
                    category,
                    field.Description?.Trim(),
                    field.Required,
                    field.Placeholder?.Trim(),
                    field.HelpText?.Trim(),
                    field.MinLength,
                    field.MaxLength,
                    field.Min,
                    field.Max,
                    field.Step,
                    field.Pattern?.Trim(),
                    field.RestartRequired,
                    field.Sensitive,
                    string.IsNullOrWhiteSpace(field.SyncPolicy) ? "device-local" : field.SyncPolicy.Trim().ToLowerInvariant(),
                    field.DefaultValue?.Clone(),
                    field.VisibleWhen,
                    options,
                    secretOptions));
        }

        return results;
    }

    private static IReadOnlyList<PluginPreferenceOptionResponse> NormalizeOptions(
        string key,
        IReadOnlyList<PluginManifestPreferenceOption>? options,
        string type,
        List<string> issues)
    {
        if (type is not ("enum" or "multienum"))
        {
            return [];
        }

        if (options is null || options.Count == 0)
        {
            issues.Add($"Preference field '{key}' must declare options.");
            return [];
        }

        if (options.Count > MaxOptionsCount)
        {
            issues.Add($"Preference field '{key}' exceeds the supported option limit of {MaxOptionsCount}.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PluginPreferenceOptionResponse>();
        foreach (var option in options.Take(MaxOptionsCount))
        {
            var value = option.Value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
            {
                issues.Add($"Preference field '{key}' contains an invalid or duplicate option value.");
                continue;
            }

            var label = option.Label?.Trim();
            results.Add(new PluginPreferenceOptionResponse(value, string.IsNullOrWhiteSpace(label) ? value : label, option.Description?.Trim(), option.IsDefault));
        }

        return results;
    }

    private static NormalizedSecretOptions? NormalizeSecretOptions(string type, PluginManifestPreferenceSecretOptions? options)
    {
        if (type != "secret")
        {
            return null;
        }

        var protection = NormalizeProtectionLevel(options?.Protection) ?? "protected";
        return new NormalizedSecretOptions(protection, options?.RevealInUi ?? false, options?.AllowCopy ?? false);
    }

    private PluginPreferenceFieldResponse BuildFieldResponse(
        NormalizedField field,
        IReadOnlyDictionary<string, NormalizedField> fieldsByKey,
        IReadOnlyDictionary<string, PluginPreferenceStoreRecord> storedValues,
        int schemaVersion)
    {
        var snapshot = BuildValueSnapshot(field, storedValues, schemaVersion);
        var isVisible = EvaluateVisibility(field, fieldsByKey, storedValues, schemaVersion);

        return new PluginPreferenceFieldResponse(
            field.Key,
            field.Label,
            field.Type,
            field.Category,
            field.Description,
            field.Required,
            field.Placeholder,
            field.HelpText,
            field.MinLength,
            field.MaxLength,
            field.Min,
            field.Max,
            field.Step,
            field.Pattern,
            field.RestartRequired,
            field.Sensitive,
            field.SyncPolicy,
            isVisible,
            field.DefaultValue,
            field.Options,
            field.SecretOptions is null
                ? null
                : new PluginPreferenceSecretPolicyResponse(
                    field.SecretOptions.RequestedProtection,
                    field.SecretOptions.RevealInUi,
                    field.SecretOptions.AllowCopy,
                    IsProtectionSupported(field.SecretOptions.RequestedProtection)),
            snapshot);
    }

    private PluginPreferenceValueSnapshotResponse BuildValueSnapshot(
        NormalizedField field,
        IReadOnlyDictionary<string, PluginPreferenceStoreRecord> storedValues,
        int schemaVersion)
    {
        if (field.IsSecret)
        {
            if (string.Equals(field.RequestedProtectionLevel, "session", StringComparison.OrdinalIgnoreCase)
                && SessionSecrets.ContainsKey(GetSessionSecretKey(field.PluginId, field.Key)))
            {
                return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "set", true, false, true, "session", null);
            }

            if (!storedValues.TryGetValue(field.Key, out var secretRecord))
            {
                return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "unset", false, false, true, field.RequestedProtectionLevel, null);
            }

            var decrypted = string.IsNullOrWhiteSpace(secretRecord.SecretPayload)
                ? null
                : _cipher.TryDecrypt(secretRecord.SecretPayload);
            if (decrypted is null)
            {
                return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "invalid", true, false, true, secretRecord.ProtectionLevel, "Stored secret could not be decrypted.");
            }

            return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "set", true, false, true, secretRecord.ProtectionLevel, secretRecord.ValidationError);
        }

        if (storedValues.TryGetValue(field.Key, out var record))
        {
            if (string.IsNullOrWhiteSpace(record.JsonValue))
            {
                return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "invalid", true, false, false, null, "Stored value payload is missing.");
            }

            try
            {
                using var document = JsonDocument.Parse(record.JsonValue);
                var validation = ValidateValue(field, document.RootElement.Clone(), allowDefaultValue: false);
                if (!validation.Success)
                {
                    return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "invalid", true, false, false, null, validation.Error);
                }

                return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "set", true, false, false, null, record.ValidationError, validation.NormalizedElement!.Value);
            }
            catch (Exception ex)
            {
                return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "invalid", true, false, false, null, ex.Message);
            }
        }

        if (field.DefaultValue is { } defaultValue
            && defaultValue.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            var validation = ValidateValue(field, defaultValue, allowDefaultValue: true);
            if (!validation.Success)
            {
                return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "invalid", false, true, false, null, validation.Error);
            }

            return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "set", false, true, false, null, null, validation.NormalizedElement!.Value);
        }

        return new PluginPreferenceValueSnapshotResponse(field.Key, field.Type, "unset", false, false, false, null, null);
    }

    private bool EvaluateVisibility(
        NormalizedField field,
        IReadOnlyDictionary<string, NormalizedField> fieldsByKey,
        IReadOnlyDictionary<string, PluginPreferenceStoreRecord> storedValues,
        int schemaVersion)
    {
        if (field.VisibleWhen is null)
        {
            return true;
        }

        if (!fieldsByKey.TryGetValue(NormalizeKey(field.VisibleWhen.Field), out var dependencyField))
        {
            return true;
        }

        var dependency = BuildValueSnapshot(
            dependencyField,
            storedValues,
            schemaVersion);
        if (dependency.Value is null || field.VisibleWhen.EqualsValue is null)
        {
            return true;
        }

        return JsonElement.DeepEquals(dependency.Value.Value, field.VisibleWhen.EqualsValue.Value);
    }

    private static ValidationResult ValidateValue(NormalizedField field, JsonElement value, bool allowDefaultValue)
    {
        switch (field.Type)
        {
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return ValidationResult.Fail("Expected a boolean value.");
                }
                return ValidationResult.SuccessWith(value.Clone());
            case "int":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var integerValue))
                {
                    return ValidationResult.Fail("Expected an integer value.");
                }
                if (field.Min is { } minInt && integerValue < minInt)
                {
                    return ValidationResult.Fail($"Value must be at least {minInt}.");
                }
                if (field.Max is { } maxInt && integerValue > maxInt)
                {
                    return ValidationResult.Fail($"Value must be at most {maxInt}.");
                }
                return ValidationResult.SuccessWith(
                    JsonSerializer.SerializeToElement(
                        integerValue,
                        PluginPreferenceValueJsonContext.Default.Int64));
            case "double":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var doubleValue))
                {
                    return ValidationResult.Fail("Expected a numeric value.");
                }
                if (field.Min is { } minDouble && doubleValue < minDouble)
                {
                    return ValidationResult.Fail($"Value must be at least {minDouble}.");
                }
                if (field.Max is { } maxDouble && doubleValue > maxDouble)
                {
                    return ValidationResult.Fail($"Value must be at most {maxDouble}.");
                }
                return ValidationResult.SuccessWith(
                    JsonSerializer.SerializeToElement(
                        doubleValue,
                        PluginPreferenceValueJsonContext.Default.Double));
            case "string":
            case "text":
            case "url":
            case "duration":
            case "secret":
                return ValidateStringLike(field, value, allowDefaultValue);
            case "enum":
                {
                    var stringValidation = ValidateStringLike(field, value, allowDefaultValue);
                    if (!stringValidation.Success)
                    {
                        return stringValidation;
                    }

                    var selected = stringValidation.NormalizedElement!.Value.GetString() ?? string.Empty;
                    if (!field.OptionValues.Contains(selected, StringComparer.OrdinalIgnoreCase))
                    {
                        return ValidationResult.Fail("Selected option is not allowed.");
                    }

                    return stringValidation;
                }
            case "multienum":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    return ValidationResult.Fail("Expected an array of option values.");
                }

                var values = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return ValidationResult.Fail("Multi-select values must be strings.");
                    }

                    var optionValue = item.GetString()?.Trim() ?? string.Empty;
                    if (!field.OptionValues.Contains(optionValue, StringComparer.OrdinalIgnoreCase))
                    {
                        return ValidationResult.Fail($"Option '{optionValue}' is not allowed.");
                    }

                    if (!values.Contains(optionValue, StringComparer.OrdinalIgnoreCase))
                    {
                        values.Add(optionValue);
                    }
                }

                return ValidationResult.SuccessWith(
                    JsonSerializer.SerializeToElement(
                        values,
                        PluginPreferenceValueJsonContext.Default.ListString));
            case "json":
                if (value.GetRawText().Length > MaxStoredJsonLength)
                {
                    return ValidationResult.Fail($"JSON value exceeds the {MaxStoredJsonLength}-byte limit.");
                }
                return ValidationResult.SuccessWith(value.Clone());
            default:
                return ValidationResult.Fail($"Unsupported field type '{field.Type}'.");
        }
    }

    private static ValidationResult ValidateStringLike(NormalizedField field, JsonElement value, bool allowDefaultValue)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return ValidationResult.Fail("Expected a string value.");
        }

        var text = value.GetString()?.Trim() ?? string.Empty;
        if (!allowDefaultValue && field.Required && string.IsNullOrWhiteSpace(text))
        {
            return ValidationResult.Fail("A value is required.");
        }

        if (field.MinLength is { } minLength && text.Length < minLength)
        {
            return ValidationResult.Fail($"Value must be at least {minLength} characters.");
        }

        if (field.MaxLength is { } maxLength && text.Length > maxLength)
        {
            return ValidationResult.Fail($"Value must be at most {maxLength} characters.");
        }

        if (text.Length > MaxDefaultStringLength && field.DefaultValue is not null)
        {
            return ValidationResult.Fail($"Default value exceeds the supported length of {MaxDefaultStringLength} characters.");
        }

        if (!string.IsNullOrWhiteSpace(field.Pattern) && !Regex.IsMatch(text, field.Pattern))
        {
            return ValidationResult.Fail("Value does not match the required pattern.");
        }

        if (field.Type == "url"
            && !string.IsNullOrWhiteSpace(text)
            && !Uri.TryCreate(text, UriKind.Absolute, out _))
        {
            return ValidationResult.Fail("Value must be an absolute URL.");
        }

        if (field.Type == "duration"
            && !string.IsNullOrWhiteSpace(text)
            && !TimeSpan.TryParse(text, out _))
        {
            return ValidationResult.Fail("Value must be a valid duration.");
        }

        return ValidationResult.SuccessWith(
            JsonSerializer.SerializeToElement(
                text,
                PluginPreferenceValueJsonContext.Default.String));
    }

    private static bool IsProtectionSupported(string protectionLevel)
    {
        return SupportedProtectionLevels.Contains(protectionLevel, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeKey(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string? NormalizeProtectionLevel(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string GetSessionSecretKey(string pluginId, string fieldKey) => $"{pluginId}:{fieldKey}";

    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)*$")]
    private static partial Regex PreferenceKeyRegex();

    private sealed record ValidationResult(bool Success, JsonElement? NormalizedElement, string? Error)
    {
        public static ValidationResult SuccessWith(JsonElement value) => new(true, value, null);
        public static ValidationResult Fail(string error) => new(false, null, error);
    }

    private sealed record SchemaData(
        int SchemaVersion,
        IReadOnlyList<PluginPreferenceCategoryResponse> Categories,
        IReadOnlyList<NormalizedField> Fields,
        IReadOnlyDictionary<string, PluginPreferenceStoreRecord> StoredValues,
        IReadOnlyList<string> Issues);

    private sealed record NormalizedSecretOptions(string RequestedProtection, bool RevealInUi, bool AllowCopy);

    private sealed record NormalizedField(
        string PluginId,
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
        JsonElement? DefaultValue,
        PluginManifestPreferenceVisibilityRule? VisibleWhen,
        IReadOnlyList<PluginPreferenceOptionResponse> Options,
        NormalizedSecretOptions? SecretOptions)
    {
        public bool IsSecret => string.Equals(Type, "secret", StringComparison.OrdinalIgnoreCase);
        public string RequestedProtectionLevel => SecretOptions?.RequestedProtection ?? "protected";
        public IReadOnlyList<string> OptionValues => Options.Select(item => item.Value).ToList();
    }
}