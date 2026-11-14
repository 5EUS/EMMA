using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Api.Endpoints;

/// <summary>
/// Reads the media tree. Nodes are addressed by their canonical <c>plugin:id</c> reference in a query
/// parameter, because a plugin-local id is opaque text that may contain any character, including slashes.
/// </summary>
internal static class NodeEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/nodes", GetAsync);
        routes.MapGet("/nodes/children", ChildrenAsync);
        routes.MapPost("/nodes/enrich", EnrichAsync);
        routes.MapPost("/nodes/refresh_cover", RefreshCoverAsync);
        routes.MapGet("/nodes/thumbnail", ThumbnailAsync);
        routes.MapPost("/cache/clear", ClearCacheAsync);
    }

    private static async Task<IResult> ThumbnailAsync(HttpContext context, EmmaRuntime runtime, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        if (!node.IsSuccess)
        {
            return ApiResults.Failure(node.Error);
        }

        Outcome<AssetBytes> image = await runtime.ReadThumbnailAsync(node.Value, cancellationToken).ConfigureAwait(false);
        return image.IsSuccess
            ? new AssetContentResult(image.Value.Mime, image.Value.Bytes, range: null, acceptsRanges: false)
            : ApiResults.Failure(image.Error);
    }

    private static async Task<IResult> ClearCacheAsync(EmmaRuntime runtime, CancellationToken cancellationToken)
    {
        await runtime.ClearCacheAsync(cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(new CacheClearedResponse(Cleared: true), ApiJsonContext.Default.CacheClearedResponse);
    }

    private static async Task<IResult> GetAsync(HttpContext context, CatalogPipeline catalog, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<NodeRef>> references = RequestReader.Nodes(context.Request, "ref");
        if (!references.IsSuccess)
        {
            return ApiResults.Failure(references.Error);
        }

        Outcome<IReadOnlyList<Node>> nodes = await catalog.GetAsync(references.Value, cancellationToken).ConfigureAwait(false);
        return nodes.IsSuccess
            ? ApiResults.Ok(new NodeListResponse([.. nodes.Value.Select(NodeMapper.ToDto)]), ApiJsonContext.Default.NodeListResponse)
            : ApiResults.Failure(nodes.Error);
    }

    private static async Task<IResult> ChildrenAsync(HttpContext context, CatalogPipeline catalog, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> parent = RequestReader.Node(context.Request, "ref");
        Outcome<PageRequest> page = RequestReader.Page(context.Request);
        if (!parent.IsSuccess || !page.IsSuccess)
        {
            return ApiResults.Failure((parent.Error ?? page.Error)!);
        }

        Outcome<NodePage> nodes = await catalog.ChildrenAsync(parent.Value, page.Value, cancellationToken).ConfigureAwait(false);
        return nodes.IsSuccess
            ? ApiResults.Ok(NodeMapper.ToDto(nodes.Value), ApiJsonContext.Default.NodePageResponse)
            : ApiResults.Failure(nodes.Error);
    }

    private static async Task<IResult> EnrichAsync(HttpContext context, CatalogPipeline catalog, CancellationToken cancellationToken)
    {
        Outcome<IReadOnlyList<NodeRef>> references = RequestReader.Nodes(context.Request, "ref");
        if (!references.IsSuccess)
        {
            return ApiResults.Failure(references.Error);
        }

        Outcome<IReadOnlyList<Node>> nodes = await catalog.EnrichAsync(references.Value, cancellationToken).ConfigureAwait(false);
        return nodes.IsSuccess
            ? ApiResults.Ok(new NodeListResponse([.. nodes.Value.Select(NodeMapper.ToDto)]), ApiJsonContext.Default.NodeListResponse)
            : ApiResults.Failure(nodes.Error);
    }

    private static async Task<IResult> RefreshCoverAsync(HttpContext context, RefreshPipeline refresh, CancellationToken cancellationToken)
    {
        Outcome<NodeRef> node = RequestReader.Node(context.Request, "ref");
        if (!node.IsSuccess)
        {
            return ApiResults.Failure(node.Error);
        }

        Outcome<Node> refreshed = await refresh.RefreshNodeAsync(node.Value, cancellationToken).ConfigureAwait(false);
        return refreshed.IsSuccess
            ? ApiResults.Ok(NodeMapper.ToDto(refreshed.Value), ApiJsonContext.Default.NodeDto)
            : ApiResults.Failure(refreshed.Error);
    }
}
