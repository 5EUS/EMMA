using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>The record of which plugins are installed and whether they may run.</summary>
public interface IPluginRegistryPort
{
    /// <summary>Inserts a plugin or replaces its record (an upgrade).</summary>
    /// <param name="plugin">The record.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the record is stored.</returns>
    Task UpsertAsync(InstalledPlugin plugin, CancellationToken cancellationToken);

    /// <summary>Finds a plugin.</summary>
    /// <param name="id">The plugin id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The record, or <see langword="null"/> when not installed.</returns>
    Task<InstalledPlugin?> GetAsync(PluginId id, CancellationToken cancellationToken);

    /// <summary>Lists every installed plugin by id.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The records.</returns>
    Task<IReadOnlyList<InstalledPlugin>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Changes a plugin's status.</summary>
    /// <param name="id">The plugin id.</param>
    /// <param name="status">The new status.</param>
    /// <param name="updatedAt">When the change happened.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the plugin was installed.</returns>
    Task<bool> SetStatusAsync(PluginId id, PluginStatus status, DateTimeOffset updatedAt, CancellationToken cancellationToken);

    /// <summary>Removes a plugin's record.</summary>
    /// <param name="id">The plugin id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the plugin was installed.</returns>
    Task<bool> RemoveAsync(PluginId id, CancellationToken cancellationToken);
}
