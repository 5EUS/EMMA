using System.Text;

using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.PluginHost.Manifest;

namespace EMMA.PluginHost.Policy;

/// <summary>
/// Grants each plugin exactly what its verified manifest asks for, and nothing to a plugin that is
/// unknown, disabled, quarantined or whose stored manifest cannot be read. It answers from an
/// in-memory snapshot (so hot paths never touch the database); call <see cref="RefreshAsync"/> after
/// an install, removal or status change.
/// </summary>
public sealed class ManifestCapabilityPolicyProvider : ICapabilityPolicyProvider
{
    private volatile IReadOnlyDictionary<PluginId, CapabilityPolicy> policies = new Dictionary<PluginId, CapabilityPolicy>();

    /// <summary>Builds the policy a manifest asks for.</summary>
    /// <param name="manifest">The verified manifest.</param>
    /// <returns>Its network allow-list and cache permission.</returns>
    public static CapabilityPolicy FromManifest(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new CapabilityPolicy(manifest.Permissions.Network, manifest.Permissions.Cache);
    }

    /// <inheritdoc/>
    public CapabilityPolicy GetPolicy(PluginId plugin) =>
        this.policies.GetValueOrDefault(plugin) ?? CapabilityPolicy.DenyAll;

    /// <summary>Rebuilds the snapshot from the registry. The new snapshot replaces the old one in one step.</summary>
    /// <param name="registry">The record of installed plugins.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plugins whose stored manifest could not be read; they are denied everything, and the host should quarantine them.</returns>
    public async Task<IReadOnlyList<PluginId>> RefreshAsync(IPluginRegistryPort registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);

        IReadOnlyList<InstalledPlugin> installed = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return this.Apply(installed);
    }

    /// <summary>Replaces the snapshot with the policies for the given installed plugins.</summary>
    /// <param name="installed">The installed plugins.</param>
    /// <returns>The plugins whose stored manifest could not be read.</returns>
    public IReadOnlyList<PluginId> Apply(IEnumerable<InstalledPlugin> installed)
    {
        Dictionary<PluginId, CapabilityPolicy> next = [];
        List<PluginId> unreadable = [];
        foreach (InstalledPlugin plugin in installed.Where(plugin => plugin.Status == PluginStatus.Enabled))
        {
            // The manifest was verified when it was installed, so a missing signature is not re-checked here.
            Outcome<PluginManifest> manifest = ManifestParser.Parse(Encoding.UTF8.GetBytes(plugin.ManifestJson), requireSignature: false);
            if (manifest.IsSuccess && manifest.Value.Id == plugin.Id)
            {
                next[plugin.Id] = FromManifest(manifest.Value);
            }
            else
            {
                unreadable.Add(plugin.Id);
            }
        }

        this.policies = next;
        return unreadable;
    }
}
