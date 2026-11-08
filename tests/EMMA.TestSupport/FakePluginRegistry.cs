using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>An in-memory <see cref="IPluginRegistryPort"/> that can be told to fail, to test rollback.</summary>
public sealed class FakePluginRegistry : IPluginRegistryPort
{
    private readonly Dictionary<PluginId, InstalledPlugin> plugins = [];

    /// <summary>Gets or sets a value indicating whether <see cref="UpsertAsync"/> throws.</summary>
    public bool FailOnUpsert { get; set; }

    /// <inheritdoc/>
    public Task UpsertAsync(InstalledPlugin plugin, CancellationToken cancellationToken)
    {
        if (this.FailOnUpsert)
        {
            throw new InvalidOperationException("The registry is unavailable.");
        }

        this.plugins[plugin.Id] = plugin;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<InstalledPlugin?> GetAsync(PluginId id, CancellationToken cancellationToken) =>
        Task.FromResult(this.plugins.GetValueOrDefault(id));

    /// <inheritdoc/>
    public Task<IReadOnlyList<InstalledPlugin>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InstalledPlugin>>([.. this.plugins.Values.OrderBy(plugin => plugin.Id.Value, StringComparer.Ordinal)]);

    /// <inheritdoc/>
    public Task<bool> SetStatusAsync(PluginId id, PluginStatus status, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (!this.plugins.TryGetValue(id, out InstalledPlugin? plugin))
        {
            return Task.FromResult(false);
        }

        this.plugins[id] = plugin with { Status = status, UpdatedAt = updatedAt };
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> RemoveAsync(PluginId id, CancellationToken cancellationToken) =>
        Task.FromResult(this.plugins.Remove(id));
}
