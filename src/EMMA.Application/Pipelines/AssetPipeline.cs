using System.Security.Cryptography;
using System.Text;

using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Turns nodes into bytes. One path serves page images, video manifests, subtitles,
/// audio and article text: resolve a node for a role, then fetch what the plugin
/// pointed at. Remote assets go through one shared, policy-checked fetch and cache.
/// </summary>
/// <param name="gateway">How to reach plugins.</param>
/// <param name="policies">What each plugin may reach.</param>
/// <param name="store">Where seen nodes are remembered; used to check roles.</param>
/// <param name="fetcher">Fetches remote assets.</param>
/// <param name="cache">Caches fetched bytes.</param>
/// <param name="executor">Runs plugin calls with retries and timeouts.</param>
/// <param name="options">Size limits.</param>
/// <param name="time">The time source.</param>
public sealed class AssetPipeline(IPluginGateway gateway, ICapabilityPolicyProvider policies, INodeStorePort store, IAssetFetcherPort fetcher, IAssetCachePort cache, PluginCallExecutor executor, AssetPipelineOptions options, TimeProvider time)
{
    /// <summary>Resolves a node to its assets for a role.</summary>
    /// <param name="node">The node.</param>
    /// <param name="role">What the assets are for.</param>
    /// <param name="hints">Optional preferences such as <c>emma.quality</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The validated assets, or why resolving failed.</returns>
    public async Task<Outcome<IReadOnlyList<Asset>>> ResolveAsync(
        NodeRef node,
        AssetRole role,
        IReadOnlyList<Prop> hints,
        CancellationToken cancellationToken)
    {
        PluginError? unsupported = await this.CheckRoleAsync(node, role, cancellationToken).ConfigureAwait(false);
        if (unsupported is not null)
        {
            return Outcome.Failure<IReadOnlyList<Asset>>(unsupported);
        }

        ResolveRequest request = new(node.Id, role, hints);
        Outcome<IReadOnlyList<Asset>> raw = await executor
            .ExecuteAsync(token => gateway.ResolveAsync(node.Plugin, request, token), cancellationToken)
            .ConfigureAwait(false);
        return raw.IsSuccess
            ? this.ValidateAssets(node.Plugin, raw.Value)
            : raw;
    }

    /// <summary>Gets the bytes of an asset returned by <see cref="ResolveAsync"/>.</summary>
    /// <param name="node">The node the asset was resolved for.</param>
    /// <param name="role">The role it was resolved for.</param>
    /// <param name="asset">The asset.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes, or why they could not be fetched.</returns>
    public async Task<Outcome<AssetPayload>> GetPayloadAsync(
        NodeRef node,
        AssetRole role,
        Asset asset,
        CancellationToken cancellationToken)
    {
        return asset.Delivery switch
        {
            InlineDelivery inline => Outcome.Success(new AssetPayload(asset.Mime, inline.Payload, time.GetUtcNow())),
            RemoteDelivery remote => await this.GetRemoteAsync(node, role, asset.Mime, remote, cancellationToken).ConfigureAwait(false),
            ProxiedDelivery proxied => await this.GetProxiedAsync(node.Plugin, asset.Mime, proxied, cancellationToken).ConfigureAwait(false),
            _ => Outcome.Failure<AssetPayload>(ErrorCode.Internal, "Unknown asset delivery."),
        };
    }

    /// <summary>Reads a byte range from a proxied asset, for example to seek in a large file.</summary>
    /// <param name="node">The node the asset belongs to.</param>
    /// <param name="asset">The proxied asset.</param>
    /// <param name="range">The range to read.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The bytes, or why they could not be read.</returns>
    public async Task<Outcome<Chunk>> ReadRangeAsync(
        NodeRef node,
        ProxiedDelivery asset,
        ByteRange range,
        CancellationToken cancellationToken)
    {
        if (range.Start < 0 || range.Length is <= 0 || range.Length > options.MaxPayloadBytes)
        {
            return Outcome.Failure<Chunk>(ErrorCode.InvalidArgument, "The byte range is invalid or too large.");
        }

        Outcome<Chunk> chunk = await executor
            .ExecuteAsync(token => gateway.ReadAsync(node.Plugin, asset.Token, range, token), cancellationToken)
            .ConfigureAwait(false);
        if (!chunk.IsSuccess)
        {
            return chunk;
        }

        long limit = range.Length ?? options.MaxPayloadBytes;
        return chunk.Value.Bytes.Length > limit
            ? Outcome.Failure<Chunk>(ErrorCode.Internal, $"Plugin {node.Plugin} returned more bytes than requested.")
            : chunk;
    }

    private static Outcome<IReadOnlyList<Asset>> Violation(PluginId plugin, string problem) =>
        Outcome.Failure<IReadOnlyList<Asset>>(ErrorCode.Internal, $"Plugin {plugin} {problem}.");

    private static string VariantOf(Uri url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)));

    private static string? DeliveryProblem(AssetDelivery delivery, CapabilityPolicy policy) => delivery switch
    {
        InlineDelivery inline when inline.Payload.Length > ContractLimits.MaxInlineAssetBytes =>
            $"returned an inline asset over {ContractLimits.MaxInlineAssetBytes} bytes",
        RemoteDelivery remote when !remote.Url.IsAbsoluteUri || !policy.AllowsNetwork(remote.Url) =>
            $"returned an asset on '{(remote.Url.IsAbsoluteUri ? remote.Url.Host : "a relative URL")}', outside its network permissions",
        ProxiedDelivery proxied when proxied.Token.Length == 0
            || Encoding.UTF8.GetByteCount(proxied.Token) > ContractLimits.MaxCursorBytes =>
            "returned a proxied asset with an empty or oversized token",
        _ => null,
    };

    private async Task<PluginError?> CheckRoleAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken)
    {
        Node? known = await store.GetAsync(node, cancellationToken).ConfigureAwait(false);
        if (known is null)
        {
            return null;
        }

        Outcome<PluginInfo> info = await executor
            .ExecuteAsync(token => gateway.DescribeAsync(node.Plugin, token), cancellationToken)
            .ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            return info.Error;
        }

        KindInfo? kind = info.Value.FindKind(known.Kind);
        return kind?.Roles.Contains(role) == false
            ? new PluginError(ErrorCode.Unsupported, $"Nodes of kind '{known.Kind}' have no '{role}' assets.")
            : null;
    }

    private Outcome<IReadOnlyList<Asset>> ValidateAssets(PluginId plugin, IReadOnlyList<Asset> assets)
    {
        CapabilityPolicy policy = policies.GetPolicy(plugin);
        foreach (Asset asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.Mime) || asset.Mime.Length > 255 || !asset.Mime.Contains('/', StringComparison.Ordinal))
            {
                return Violation(plugin, "returned an asset with a malformed mime type");
            }

            Outcome<IReadOnlyList<Prop>> props = NodeValidator.ValidateProps(plugin, "an asset", asset.Props);
            if (!props.IsSuccess)
            {
                return Outcome.Failure<IReadOnlyList<Asset>>(props.Error);
            }

            string? problem = DeliveryProblem(asset.Delivery, policy);
            if (problem is not null)
            {
                return Violation(plugin, problem);
            }
        }

        return Outcome.Success(assets);
    }

    private async Task<Outcome<AssetPayload>> GetRemoteAsync(
        NodeRef node,
        AssetRole role,
        string mime,
        RemoteDelivery remote,
        CancellationToken cancellationToken)
    {
        AssetKey key = new(node, role, VariantOf(remote.Url));
        AssetPayload? cached = await cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return Outcome.Success(cached);
        }

        if (remote.ExpiresAt is { } expiresAt && expiresAt <= time.GetUtcNow())
        {
            return Outcome.Failure<AssetPayload>(ErrorCode.NotFound, "The asset link has expired; resolve the node again.");
        }

        CapabilityPolicy policy = policies.GetPolicy(node.Plugin);
        Outcome<AssetPayload> fetched = await executor
            .ExecuteAsync(
                token => fetcher.FetchAsync(remote, policy, options.MaxPayloadBytes, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (!fetched.IsSuccess)
        {
            return fetched;
        }

        AssetPayload payload = fetched.Value with { Mime = mime };
        await cache.SetAsync(key, payload, cancellationToken).ConfigureAwait(false);
        return Outcome.Success(payload);
    }

    private async Task<Outcome<AssetPayload>> GetProxiedAsync(
        PluginId plugin,
        string mime,
        ProxiedDelivery proxied,
        CancellationToken cancellationToken)
    {
        Outcome<Chunk> chunk = await executor
            .ExecuteAsync(token => gateway.ReadAsync(plugin, proxied.Token, range: null, token), cancellationToken)
            .ConfigureAwait(false);
        if (!chunk.IsSuccess)
        {
            return Outcome.Failure<AssetPayload>(chunk.Error);
        }

        return chunk.Value.Bytes.Length > options.MaxPayloadBytes
            ? Outcome.Failure<AssetPayload>(ErrorCode.Internal, $"Plugin {plugin} returned an asset over the size limit.")
            : Outcome.Success(new AssetPayload(mime, chunk.Value.Bytes, time.GetUtcNow()));
    }
}
