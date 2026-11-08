using System.Globalization;
using System.Net.Http.Headers;

using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Domain;

using Microsoft.Extensions.Primitives;

namespace EMMA.Api.Endpoints;

/// <summary>
/// Resolves nodes to assets and serves their bytes. The API never reveals where an asset really lives (a
/// remote URL may carry cookies or referer rules, a proxied token is private to the plugin): clients ask
/// for an asset by index and the host fetches it under the plugin's network policy.
/// </summary>
internal static class AssetEndpoints
{
    private const int MaxIndex = 1023;

    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/assets", ListAsync);
        routes.MapGet("/assets/content", ContentAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext context, AssetPipeline assets, CancellationToken cancellationToken)
    {
        Outcome<(NodeRef Node, AssetRole Role)> target = ReadTarget(context.Request);
        if (!target.IsSuccess)
        {
            return ApiResults.Failure(target.Error);
        }

        Outcome<IReadOnlyList<Asset>> resolved = await assets.ResolveAsync(target.Value.Node, target.Value.Role, [], cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess
            ? ApiResults.Ok(ToResponse(resolved.Value), ApiJsonContext.Default.AssetListResponse)
            : ApiResults.Failure(resolved.Error);
    }

    private static async Task<IResult> ContentAsync(HttpContext context, AssetPipeline assets, CancellationToken cancellationToken)
    {
        Outcome<(NodeRef Node, AssetRole Role)> target = ReadTarget(context.Request);
        Outcome<int> index = ReadIndex(context.Request);
        if (!target.IsSuccess || !index.IsSuccess)
        {
            return ApiResults.Failure((target.Error ?? index.Error)!);
        }

        (NodeRef node, AssetRole role) = target.Value;
        Outcome<IReadOnlyList<Asset>> resolved = await assets.ResolveAsync(node, role, [], cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSuccess)
        {
            return ApiResults.Failure(resolved.Error);
        }

        if (index.Value >= resolved.Value.Count)
        {
            return ApiResults.Failure(ErrorCode.NotFound, "The node has no asset at that index.");
        }

        Asset asset = resolved.Value[index.Value];
        return asset.Delivery is ProxiedDelivery proxied && ReadRange(context.Request) is { } range
            ? await RangeAsync(assets, node, asset, proxied, range, cancellationToken).ConfigureAwait(false)
            : await WholeAsync(assets, node, role, asset, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> WholeAsync(AssetPipeline assets, NodeRef node, AssetRole role, Asset asset, CancellationToken cancellationToken)
    {
        Outcome<AssetPayload> payload = await assets.GetPayloadAsync(node, role, asset, cancellationToken).ConfigureAwait(false);
        return payload.IsSuccess
            ? new AssetContentResult(payload.Value.Mime, payload.Value.Payload, range: null, acceptsRanges: asset.Delivery is ProxiedDelivery)
            : ApiResults.Failure(payload.Error);
    }

    private static async Task<IResult> RangeAsync(AssetPipeline assets, NodeRef node, Asset asset, ProxiedDelivery proxied, ByteRange range, CancellationToken cancellationToken)
    {
        Outcome<Chunk> chunk = await assets.ReadRangeAsync(node, proxied, range, cancellationToken).ConfigureAwait(false);
        if (!chunk.IsSuccess)
        {
            return ApiResults.Failure(chunk.Error);
        }

        return chunk.Value.Bytes.IsEmpty
            ? ApiResults.Error(StatusCodes.Status416RangeNotSatisfiable, "range_not_satisfiable", "The range starts beyond the end of the asset.")
            : new AssetContentResult(asset.Mime, chunk.Value.Bytes, new ContentRange(range.Start, chunk.Value.Total), acceptsRanges: true);
    }

    private static Outcome<(NodeRef Node, AssetRole Role)> ReadTarget(HttpRequest request)
    {
        Outcome<NodeRef> node = RequestReader.Node(request, "ref");
        Outcome<string> role = RequestReader.Required(request, "role");
        if (!node.IsSuccess || !role.IsSuccess)
        {
            return Outcome.Failure<(NodeRef, AssetRole)>((node.Error ?? role.Error)!);
        }

        return AssetRole.TryCreate(role.Value, out AssetRole parsed)
            ? Outcome.Success((node.Value, parsed))
            : Outcome.Failure<(NodeRef, AssetRole)>(ErrorCode.InvalidArgument, "'role' is not a valid asset role.");
    }

    private static Outcome<int> ReadIndex(HttpRequest request)
    {
        StringValues values = request.Query["index"];
        if (values.Count == 0)
        {
            return Outcome.Success(0);
        }

        return values.Count == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && index <= MaxIndex
            ? Outcome.Success(index)
            : Outcome.Failure<int>(ErrorCode.InvalidArgument, $"'index' must be a whole number from 0 to {MaxIndex}.");
    }

    /// <summary>Reads a single <c>bytes=start-</c> or <c>bytes=start-end</c> range; anything else is ignored and the whole asset is sent.</summary>
    private static ByteRange? ReadRange(HttpRequest request)
    {
        if (!RangeHeaderValue.TryParse(request.Headers.Range.ToString(), out RangeHeaderValue? header)
            || !string.Equals(header.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || header.Ranges.Count != 1)
        {
            return null;
        }

        RangeItemHeaderValue item = header.Ranges.Single();
        if (item.From is not { } start || (item.To is { } end && end < start))
        {
            return null;
        }

        return new ByteRange(start, item.To is { } last ? last - start + 1 : null);
    }

    private static AssetListResponse ToResponse(IReadOnlyList<Asset> assets) => new(
        [.. assets.Select((asset, index) => new AssetDto(
            index,
            asset.Mime,
            asset.Size,
            DeliveryName(asset.Delivery),
            PropMapper.ToDtos(asset.Props)))]);

    private static string DeliveryName(AssetDelivery delivery) => delivery switch
    {
        InlineDelivery => "inline",
        RemoteDelivery => "remote",
        _ => "proxied",
    };
}
