using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>An in-memory <see cref="IPluginSettingsPort"/>.</summary>
public sealed class FakePluginSettings : IPluginSettingsPort
{
    private readonly Dictionary<PluginId, IReadOnlyDictionary<string, IReadOnlyList<string>>> values = [];

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
    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(PluginId plugin, CancellationToken cancellationToken) =>
        Task.FromResult(this.values.GetValueOrDefault(plugin) ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
}
