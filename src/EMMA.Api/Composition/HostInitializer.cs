using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.Cache;
using EMMA.PluginHost.Policy;
using EMMA.Storage;

namespace EMMA.Api.Composition;

/// <summary>Prepares the host before it serves a request: the database, the asset cache and each plugin's permissions.</summary>
/// <param name="storage">Creates and migrates the database.</param>
/// <param name="cache">The asset cache.</param>
/// <param name="policies">What each plugin may reach.</param>
/// <param name="registry">The installed plugins.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The host's logger.</param>
internal sealed class HostInitializer(
    StorageInitializer storage,
    BoundedAssetCache cache,
    ManifestCapabilityPolicyProvider policies,
    IPluginRegistryPort registry,
    TimeProvider time,
    ILogger<HostInitializer> logger)
{
    /// <summary>Prepares the host.</summary>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    /// <returns>A task that completes when the host is ready to serve.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await storage.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await cache.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // A plugin whose stored manifest cannot be read is denied everything by the policy snapshot; take it out of service too.
        IReadOnlyList<PluginId> unreadable = await policies.RefreshAsync(registry, cancellationToken).ConfigureAwait(false);
        foreach (PluginId plugin in unreadable)
        {
            logger.LogError("Quarantining plugin {Plugin}: its stored manifest cannot be read.", plugin.Value);
            await registry.SetStatusAsync(plugin, PluginStatus.Quarantined, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
    }
}
