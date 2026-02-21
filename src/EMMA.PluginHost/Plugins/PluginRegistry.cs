namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Records the latest handshake status for a plugin.
/// </summary>
public sealed record PluginHandshakeStatus(
    bool Success,
    string Message,
    string? Version,
    DateTimeOffset Timestamp,
    IReadOnlyList<string> Capabilities,
    int CpuBudgetMs,
    int MemoryMb,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> Paths);

/// <summary>
/// Represents a plugin manifest with its last handshake result.
/// </summary>
public sealed record PluginRecord(PluginManifest Manifest, PluginHandshakeStatus Status);

/// <summary>
/// In-memory registry for plugin manifests and handshake results.
/// </summary>
public sealed class PluginRegistry
{
    private readonly Dictionary<string, PluginRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Adds or updates a plugin record.
    /// </summary>
    public void Upsert(PluginRecord record)
    {
        lock (_lock)
        {
            _records[record.Manifest.Id] = record;
        }
    }

    /// <summary>
    /// Returns a point-in-time snapshot of all plugin records.
    /// </summary>
    public IReadOnlyList<PluginRecord> GetSnapshot()
    {
        lock (_lock)
        {
            return [.. _records.Values];
        }
    }
}
