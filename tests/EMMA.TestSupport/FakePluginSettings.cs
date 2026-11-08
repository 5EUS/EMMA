using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>An in-memory <see cref="IPluginSettingsPort"/>.</summary>
public sealed class FakePluginSettings : IPluginSettingsPort
{
    private readonly Dictionary<PluginId, IReadOnlyDictionary<string, IReadOnlyList<string>>> values = [];
    private readonly HashSet<(PluginId Plugin, string Key)> secrets = [];

    /// <summary>Sets the values a plugin will receive.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="settings">The values by key.</param>
    /// <returns>This instance.</returns>
    public FakePluginSettings With(PluginId plugin, IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        this.values[plugin] = settings;
        return this;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredSetting>> ListAsync(PluginId plugin, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredSetting>>(
        [
            .. (this.values.GetValueOrDefault(plugin) ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new StoredSetting(pair.Key, this.secrets.Contains((plugin, pair.Key)), this.secrets.Contains((plugin, pair.Key)) ? [] : pair.Value)),
        ]);

    /// <inheritdoc/>
    public Task ApplyAsync(PluginId plugin, IReadOnlyList<SettingWrite> writes, CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<string>> current = new(this.values.GetValueOrDefault(plugin) ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (SettingWrite write in writes)
        {
            if (write.Values.Count == 0)
            {
                current.Remove(write.Key);
                this.secrets.Remove((plugin, write.Key));
            }
            else
            {
                current[write.Key] = write.Values;
                if (write.IsSecret)
                {
                    this.secrets.Add((plugin, write.Key));
                }
            }
        }

        this.values[plugin] = current;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(PluginId plugin, CancellationToken cancellationToken) =>
        Task.FromResult(this.values.GetValueOrDefault(plugin) ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}
