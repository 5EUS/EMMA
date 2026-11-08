using EMMA.Domain;

namespace EMMA.Runtime;

/// <summary>Operations that combine several ports in the same way for every host.</summary>
public sealed partial class EmmaRuntime
{
    /// <summary>Gets an installed plugin and, when it is enabled, what it declares.</summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plugin, <c>not-found</c> when it is not installed, or why it could not be asked.</returns>
    public async Task<Outcome<PluginDetail>> DescribePluginAsync(PluginId plugin, CancellationToken cancellationToken)
    {
        InstalledPlugin? installed = await this.Registry.GetAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (installed is null)
        {
            return Outcome.Failure<PluginDetail>(ErrorCode.NotFound, "That plugin is not installed.");
        }

        if (installed.Status != PluginStatus.Enabled)
        {
            return Outcome.Success(new PluginDetail(installed, null));
        }

        Outcome<PluginInfo> info = await this.Catalog.DescribeAsync(plugin, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Outcome.Success(new PluginDetail(installed, info.Value))
            : Outcome.Failure<PluginDetail>(info.Error);
    }

    /// <summary>Resolves a node for a role, picks one of the assets and reads its bytes, or a range of them.</summary>
    /// <param name="node">The node.</param>
    /// <param name="role">What the asset is for.</param>
    /// <param name="index">Which of the resolved assets to read.</param>
    /// <param name="range">A byte range to read; honoured only for assets the plugin serves itself, otherwise the whole asset is read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes, or why they could not be read.</returns>
    public async Task<Outcome<AssetBytes>> ReadAssetAsync(NodeRef node, AssetRole role, int index, ByteRange? range, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<Asset>> resolved = await this.Assets.ResolveAsync(node, role, [], cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Outcome.Failure<AssetBytes>(resolved.Error);
        }

        if (index < 0 || index >= resolved.Value.Count)
        {
            return Outcome.Failure<AssetBytes>(ErrorCode.NotFound, "The node has no asset at that index.");
        }

        Asset asset = resolved.Value[index];
        if (asset.Delivery is ProxiedDelivery proxied && range is not null)
        {
            Outcome<Chunk> chunk = await this.Assets.ReadRangeAsync(node, proxied, range, cancellationToken).ConfigureAwait(false);
            return chunk.IsSuccess
                ? Outcome.Success(new AssetBytes(asset.Mime, chunk.Value.Bytes, range.Start, chunk.Value.Total, AcceptsRanges: true))
                : Outcome.Failure<AssetBytes>(chunk.Error);
        }

        Outcome<AssetPayload> payload = await this.Assets.GetPayloadAsync(node, role, asset, cancellationToken).ConfigureAwait(false);
        return payload.IsSuccess
            ? Outcome.Success(new AssetBytes(payload.Value.Mime, payload.Value.Payload, RangeStart: null, Total: null, asset.Delivery is ProxiedDelivery))
            : Outcome.Failure<AssetBytes>(payload.Error);
    }
}
