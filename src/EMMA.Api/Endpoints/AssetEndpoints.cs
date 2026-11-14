using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Api.Endpoints;

/// <summary>
/// Resolves nodes to assets and serves their bytes. The API never reveals where an asset really lives (a
/// remote URL may carry cookies or referer rules, a proxied token is private to the plugin): clients ask
/// for an asset by index and the host fetches it under the plugin's network policy.
/// </summary>
internal static class AssetEndpoints
{
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
        Outcome<string?> hint = RequestReader.Hint(context.Request);
        if (!target.IsSuccess || !hint.IsSuccess)
        {
            return ApiResults.Failure((target.Error ?? hint.Error)!);
        }

        Outcome<IReadOnlyList<Asset>> resolved = await assets.ResolveAsync(target.Value.Node, target.Value.Role, AssetHint.ToProps(hint.Value), cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess
            ? ApiResults.Ok(AssetMapper.ToResponse(resolved.Value), ApiJsonContext.Default.AssetListResponse)
            : ApiResults.Failure(resolved.Error);
    }

    private static async Task<IResult> ContentAsync(HttpContext context, EmmaRuntime runtime, CancellationToken cancellationToken)
    {
        Outcome<(NodeRef Node, AssetRole Role)> target = ReadTarget(context.Request);
        Outcome<int> index = RequestReader.Index(context.Request);
        Outcome<string?> hint = RequestReader.Hint(context.Request);
        if (!target.IsSuccess || !index.IsSuccess || !hint.IsSuccess)
        {
            return ApiResults.Failure((target.Error ?? index.Error ?? hint.Error)!);
        }

        Outcome<AssetBytes> content = await runtime
            .ReadAssetAsync(target.Value.Node, target.Value.Role, index.Value, RequestReader.Range(context.Request), hint.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return ApiResults.Failure(content.Error);
        }

        AssetBytes bytes = content.Value;
        if (bytes.RangeNotSatisfiable)
        {
            return ApiResults.Error(StatusCodes.Status416RangeNotSatisfiable, "range_not_satisfiable", "The range starts beyond the end of the asset.");
        }

        ContentRange? range = bytes.RangeStart is { } start ? new ContentRange(start, bytes.Total) : null;
        return new AssetContentResult(bytes.Mime, bytes.Bytes, range, bytes.AcceptsRanges);
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
}
