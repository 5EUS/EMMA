namespace EMMA.Application.Ports;

public sealed record PluginPreferenceStoreRecord(
    string PluginId,
    string FieldKey,
    string FieldType,
    string ProtectionLevel,
    bool IsSecret,
    string? JsonValue,
    string? SecretPayload,
    string ValueState,
    int SchemaVersion,
    string? ValidationError,
    string UpdatedAtUtc);

public interface IPluginPreferenceStore
{
    Task<IReadOnlyList<PluginPreferenceStoreRecord>> ListByPluginAsync(string pluginId, CancellationToken cancellationToken);
    Task<PluginPreferenceStoreRecord?> GetAsync(string pluginId, string fieldKey, CancellationToken cancellationToken);
    Task UpsertAsync(PluginPreferenceStoreRecord record, CancellationToken cancellationToken);
    Task DeleteAsync(string pluginId, string fieldKey, CancellationToken cancellationToken);
}