using System.Text;

using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Policy;

/// <summary>
/// Grants each plugin exactly what its verified manifest asks for, and nothing to a plugin that is
/// unknown, disabled, quarantined or whose stored manifest cannot be read. It answers from an
/// in-memory snapshot (so hot paths never touch the database); call <see cref="RefreshAsync(IPluginRegistryPort, IPluginSettingsPort, CancellationToken)"/> after
/// an install, removal or status change.
/// </summary>
public sealed class ManifestCapabilityPolicyProvider : ICapabilityPolicyProvider
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoSettings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private volatile IReadOnlyDictionary<PluginId, CapabilityPolicy> policies = new Dictionary<PluginId, CapabilityPolicy>();

    /// <summary>Builds the policy a manifest asks for.</summary>
    /// <param name="manifest">The verified manifest.</param>
    /// <param name="settings">The user's settings for the plugin, by key; the hosts of the URL settings the manifest names are added.</param>
    /// <returns>Its network allow-list and cache permission.</returns>
    public static CapabilityPolicy FromManifest(PluginManifest manifest, IReadOnlyDictionary<string, IReadOnlyList<string>>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new CapabilityPolicy(SettingNetworkHosts.Effective(manifest.Permissions, settings ?? NoSettings), manifest.Permissions.Cache);
    }

    /// <inheritdoc/>
    public CapabilityPolicy GetPolicy(PluginId plugin) =>
        this.policies.GetValueOrDefault(plugin) ?? CapabilityPolicy.DenyAll;

    /// <summary>Rebuilds the snapshot from the registry, counting no user settings. The new snapshot replaces the old one in one step.</summary>
    /// <param name="registry">The record of installed plugins.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plugins whose stored manifest could not be read; they are denied everything, and the host should quarantine them.</returns>
    public async Task<IReadOnlyList<PluginId>> RefreshAsync(IPluginRegistryPort registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);

        IReadOnlyList<InstalledPlugin> installed = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return this.Apply(installed);
    }

    /// <summary>Rebuilds the snapshot from the registry and the user's settings. The new snapshot replaces the old one in one step.</summary>
    /// <param name="registry">The record of installed plugins.</param>
    /// <param name="settings">The user's plugin settings, read for plugins whose manifest names URL settings.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plugins whose stored manifest could not be read; they are denied everything, and the host should quarantine them.</returns>
    public async Task<IReadOnlyList<PluginId>> RefreshAsync(IPluginRegistryPort registry, IPluginSettingsPort settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settings);

        IReadOnlyList<InstalledPlugin> installed = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<PluginId, IReadOnlyDictionary<string, IReadOnlyList<string>>> values = [];
        foreach (PluginId id in installed.Where(plugin => plugin.Status == PluginStatus.Enabled && MentionsNetworkSettings(plugin.ManifestJson)).Select(plugin => plugin.Id))
        {
            values[id] = await settings.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return this.Apply(installed, values);
    }

    /// <summary>Replaces the snapshot with the policies for the given installed plugins.</summary>
    /// <param name="installed">The installed plugins.</param>
    /// <param name="settings">The user's settings by plugin; a plugin without an entry has none.</param>
    /// <returns>The plugins whose stored manifest could not be read.</returns>
    public IReadOnlyList<PluginId> Apply(
        IEnumerable<InstalledPlugin> installed,
        IReadOnlyDictionary<PluginId, IReadOnlyDictionary<string, IReadOnlyList<string>>>? settings = null)
    {
        Dictionary<PluginId, CapabilityPolicy> next = [];
        List<PluginId> unreadable = [];
        foreach (InstalledPlugin plugin in installed.Where(plugin => plugin.Status == PluginStatus.Enabled))
        {
            // The manifest was verified when it was installed, so a missing signature is not re-checked here.
            Outcome<PluginManifest> manifest = ManifestParser.Parse(Encoding.UTF8.GetBytes(plugin.ManifestJson), requireSignature: false);
            if (manifest.IsSuccess && manifest.Value.Id == plugin.Id)
            {
                next[plugin.Id] = FromManifest(manifest.Value, settings?.GetValueOrDefault(plugin.Id));
            }
            else
            {
                unreadable.Add(plugin.Id);
            }
        }

        this.policies = next;
        return unreadable;
    }

    // A cheap test on the stored text, so only plugins that can use settings cost a database read on refresh.
    private static bool MentionsNetworkSettings(string manifestJson) =>
        manifestJson.Contains("\"networkSettings\"", StringComparison.Ordinal);
}
