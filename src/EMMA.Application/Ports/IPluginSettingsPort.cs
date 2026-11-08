using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>
/// The user's values for a plugin's declared settings. The host passes them to the plugin as text
/// (see <c>meta.setting-type</c>), with secrets already decrypted; nothing else ever sees a secret in clear.
/// </summary>
public interface IPluginSettingsPort
{
    /// <summary>Gets every value the user has set for a plugin, by setting key.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The values; a setting the user has not set is absent, and the plugin applies its default.</returns>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(PluginId plugin, CancellationToken cancellationToken);
}
