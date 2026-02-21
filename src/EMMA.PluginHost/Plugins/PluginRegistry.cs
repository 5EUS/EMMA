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

public static class PluginHandshakeDefaults
{
    public static PluginHandshakeStatus NotChecked() => new(
        false,
        "Handshake not started.",
        null,
        DateTimeOffset.UtcNow,
        [],
        0,
        0,
        [],
        []);
}

/// <summary>
/// Represents a plugin manifest with its last handshake result.
/// </summary>
public sealed record PluginRecord(
    PluginManifest Manifest,
    PluginHandshakeStatus Status,
    PluginRuntimeStatus Runtime);

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

    public void Upsert(PluginManifest manifest, PluginHandshakeStatus status, PluginRuntimeStatus? runtime)
    {
        lock (_lock)
        {
            if (_records.TryGetValue(manifest.Id, out var existing))
            {
                var mergedRuntime = runtime ?? existing.Runtime;
                _records[manifest.Id] = new PluginRecord(manifest, status, mergedRuntime);
                return;
            }

            _records[manifest.Id] = new PluginRecord(manifest, status, runtime ?? PluginRuntimeStatus.Unknown());
        }
    }

    public void UpdateRuntime(PluginManifest manifest, PluginRuntimeStatus runtime)
    {
        lock (_lock)
        {
            if (_records.TryGetValue(manifest.Id, out var existing))
            {
                _records[manifest.Id] = existing with { Runtime = runtime };
                return;
            }

            _records[manifest.Id] = new PluginRecord(manifest, PluginHandshakeDefaults.NotChecked(), runtime);
        }
    }

    public PluginRuntimeStatus GetRuntime(PluginManifest manifest)
    {
        lock (_lock)
        {
            return _records.TryGetValue(manifest.Id, out var existing)
                ? existing.Runtime
                : PluginRuntimeStatus.Unknown();
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
