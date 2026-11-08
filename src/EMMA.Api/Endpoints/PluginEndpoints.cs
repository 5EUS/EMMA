using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Api.Endpoints;

/// <summary>Lists installed plugins and searches within one. Managing plugins is not an HTTP concern.</summary>
internal static class PluginEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/plugins", ListAsync);
        routes.MapGet("/plugins/{plugin}", DetailAsync);
        routes.MapGet("/plugins/{plugin}/browse", BrowseAsync);
        routes.MapPost("/plugins/{plugin}/search", SearchAsync);
    }

    private static async Task<IResult> ListAsync(IPluginRegistryPort registry, CancellationToken cancellationToken)
    {
        IReadOnlyList<InstalledPlugin> plugins = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return ApiResults.Ok(
            new PluginListResponse([.. plugins.Select(PluginMapper.ToSummary)]),
            ApiJsonContext.Default.PluginListResponse);
    }

    private static async Task<IResult> DetailAsync(string plugin, EmmaRuntime runtime, CancellationToken cancellationToken)
    {
        Outcome<PluginId> id = RequestReader.Plugin(plugin);
        if (!id.IsSuccess)
        {
            return ApiResults.Failure(id.Error);
        }

        Outcome<PluginDetail> detail = await runtime.DescribePluginAsync(id.Value, cancellationToken).ConfigureAwait(false);
        return detail.IsSuccess
            ? ApiResults.Ok(PluginMapper.ToDetail(detail.Value.Plugin, detail.Value.Info), ApiJsonContext.Default.PluginDetailResponse)
            : ApiResults.Failure(detail.Error);
    }

    private static async Task<IResult> BrowseAsync(
        string plugin,
        HttpContext context,
        CatalogPipeline catalog,
        CancellationToken cancellationToken)
    {
        Outcome<PluginId> id = RequestReader.Plugin(plugin);
        Outcome<PageRequest> page = RequestReader.Page(context.Request);
        if (!id.IsSuccess || !page.IsSuccess)
        {
            return ApiResults.Failure((id.Error ?? page.Error)!);
        }

        Outcome<NodePage> nodes = await catalog.BrowseRootAsync(id.Value, page.Value, cancellationToken).ConfigureAwait(false);
        return nodes.IsSuccess
            ? ApiResults.Ok(NodeMapper.ToDto(nodes.Value), ApiJsonContext.Default.NodePageResponse)
            : ApiResults.Failure(nodes.Error);
    }

    private static async Task<IResult> SearchAsync(
        string plugin,
        HttpContext context,
        CatalogPipeline catalog,
        CancellationToken cancellationToken)
    {
        Outcome<PluginId> id = RequestReader.Plugin(plugin);
        if (!id.IsSuccess)
        {
            return ApiResults.Failure(id.Error);
        }

        Outcome<SearchRequest> body = await RequestReader.BodyAsync(context.Request, ApiJsonContext.Default.SearchRequest, cancellationToken).ConfigureAwait(false);
        Outcome<Query> query = body.IsSuccess ? NodeMapper.ToQuery(body.Value) : Outcome.Failure<Query>(body.Error);
        if (!query.IsSuccess)
        {
            return ApiResults.Failure(query.Error);
        }

        Outcome<NodePage> nodes = await catalog.SearchAsync(id.Value, query.Value, cancellationToken).ConfigureAwait(false);
        return nodes.IsSuccess
            ? ApiResults.Ok(NodeMapper.ToDto(nodes.Value), ApiJsonContext.Default.NodePageResponse)
            : ApiResults.Failure(nodes.Error);
    }
}
