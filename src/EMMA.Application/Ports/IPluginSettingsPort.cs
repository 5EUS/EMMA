using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// The user's values for a plugin's declared settings. The host passes them to the plugin as text
/// (see <c>meta.setting-type</c>), with secrets already decrypted; nothing else ever sees a secret in clear.
/// Settings belong to the install, not to a user: a plugin's token is the app's.
/// </summary>
public interface IPluginSettingsPort
{
    /// <summary>Gets every value the user has set for a plugin, by setting key, with secrets in clear. Only the host that opens the plugin may call this.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values; a setting the user has not set is absent, and the plugin applies its default.</returns>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(PluginId plugin, CancellationToken cancellationToken);

    /// <summary>Lists what the user has set for a plugin, for showing back to them. A secret is listed as set, without its value.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The settings that have a value, ordered by key.</returns>
    Task<IReadOnlyList<StoredSetting>> ListAsync(PluginId plugin, CancellationToken cancellationToken);

    /// <summary>Applies changes to a plugin's settings, all or none. The caller has already validated them against what the plugin declared.</summary>
    /// <param name="plugin">The plugin, which must be installed.</param>
    /// <param name="writes">The changes; a write with no values removes the setting.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the changes are stored.</returns>
    Task ApplyAsync(PluginId plugin, IReadOnlyList<SettingWrite> writes, CancellationToken cancellationToken);
}
