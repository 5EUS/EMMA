using EMMA.Application.Pipelines;
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

    /// <summary>
    /// Reads an asset: from a completed download when the node has one, otherwise by resolving the node for a role,
    /// picking one of the assets and reading its bytes, or a range of them.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="role">What the asset is for.</param>
    /// <param name="index">Which of the resolved assets to read.</param>
    /// <param name="range">A byte range to read; honoured for a stored download and for assets the plugin serves itself, otherwise the whole asset is read.</param>
    /// <param name="hint">A preference passed to the plugin, such as a quality; ignored when a stored download is served.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes, or why they could not be read.</returns>
    public async Task<Outcome<AssetBytes>> ReadAssetAsync(NodeRef node, AssetRole role, int index, ByteRange? range, string? hint, CancellationToken cancellationToken)
    {
        // A download stores the first asset a plugin offers, so only index 0 can be served from one. Nothing is asked of the plugin.
        if (index == 0 && await this.Downloads.ReadStoredAsync(node, role, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            return Outcome.Success(Slice(stored, range));
        }

        IReadOnlyList<Prop> hints = AssetHint.ToProps(hint);
        Outcome<IReadOnlyList<Asset>> resolved = await this.Assets.ResolveAsync(node, role, hints, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return Outcome.Failure<AssetBytes>(resolved.Error);
        }

        if (index < 0 || index >= resolved.Value.Count)
        {
            return Outcome.Failure<AssetBytes>(ErrorCode.NotFound, "The node has no asset at that index.");
        }

        Asset asset = resolved.Value[index];
        Outcome<AssetBytes> read = await this.ReadResolvedAsync(node, role, asset, range, cancellationToken).ConfigureAwait(false);
        return read.IsSuccess || asset.Delivery is InlineDelivery || read.Error?.Code == ErrorCode.Cancelled
            ? read
            : await this.RetryWithFreshResolutionAsync(node, role, hints, index, asset, range, read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a node's thumbnail through the host: fetched under the plugin's network permissions and kept in the disk cache.</summary>
    /// <param name="node">The node; the host must have seen it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The image, or why it could not be read.</returns>
    public async Task<Outcome<AssetBytes>> ReadThumbnailAsync(NodeRef node, CancellationToken cancellationToken)
    {
        Outcome<AssetPayload> payload = await this.Assets.GetThumbnailAsync(node, cancellationToken).ConfigureAwait(false);
        return payload.IsSuccess
            ? Outcome.Success(new AssetBytes(payload.Value.Mime, payload.Value.Payload, RangeStart: null, Total: null, AcceptsRanges: false))
            : Outcome.Failure<AssetBytes>(payload.Error);
    }

    /// <summary>
    /// Empties the caches: cached asset bytes, remembered resolutions and stored child listings. Nodes the host has seen are kept, because progress
    /// and libraries refer to them; the next read of a cleared listing asks the plugin again.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the caches are empty.</returns>
    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        await this.cache.ClearAsync(cancellationToken).ConfigureAwait(false);
        await this.resolutions.ClearAsync(cancellationToken).ConfigureAwait(false);
        await this.listings.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool SameDelivery(AssetDelivery left, AssetDelivery right) => (left, right) switch
    {
        (RemoteDelivery a, RemoteDelivery b) => a.Url == b.Url && a.Headers.SequenceEqual(b.Headers),
        (ProxiedDelivery a, ProxiedDelivery b) => string.Equals(a.Token, b.Token, StringComparison.Ordinal),
        _ => false,
    };

    private static AssetBytes Slice(AssetPayload stored, ByteRange? range)
    {
        ReadOnlyMemory<byte> bytes = stored.Payload;
        if (range is null)
        {
            return new AssetBytes(stored.Mime, bytes, RangeStart: null, Total: null, AcceptsRanges: true);
        }

        // Past the end is an empty read, which the hosts turn into "range not satisfiable".
        int start = (int)Math.Min(range.Start, bytes.Length);
        int length = (int)Math.Min(range.Length ?? bytes.Length, bytes.Length - start);
        return new AssetBytes(stored.Mime, bytes.Slice(start, length), range.Start, bytes.Length, AcceptsRanges: true);
    }

    /// <summary>
    /// A remembered resolution can hold a link or token that died before its stated expiry (a signed link, a rotated
    /// cookie, a token the plugin dropped). Ask the plugin afresh and read again, but only when it now answers
    /// differently: the same link would only fail the same way.
    /// </summary>
    private async Task<Outcome<AssetBytes>> RetryWithFreshResolutionAsync(
        NodeRef node,
        AssetRole role,
        IReadOnlyList<Prop> hints,
        int index,
        Asset failed,
        ByteRange? range,
        Outcome<AssetBytes> failure,
        CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<Asset>> fresh = await this.Assets.ResolveAsync(node, role, hints, CacheMode.Refresh, cancellationToken).ConfigureAwait(false);
        return fresh.IsSuccess && index < fresh.Value.Count && !SameDelivery(failed.Delivery, fresh.Value[index].Delivery)
            ? await this.ReadResolvedAsync(node, role, fresh.Value[index], range, cancellationToken).ConfigureAwait(false)
            : failure;
    }

    private async Task<Outcome<AssetBytes>> ReadResolvedAsync(NodeRef node, AssetRole role, Asset asset, ByteRange? range, CancellationToken cancellationToken)
    {
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
