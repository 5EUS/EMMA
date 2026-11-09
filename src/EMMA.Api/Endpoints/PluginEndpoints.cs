using EMMA.Api.Contracts;
using EMMA.Api.Http;
using EMMA.Api.Mapping;
using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Api.Endpoints;

/// <summary>Lists installed plugins, searches within one and reads or changes its settings. Managing plugins is not an HTTP concern.</summary>
internal static class PluginEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="routes">The route builder, already under <c>/v1</c>.</param>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/plugins", ListAsync);
        routes.MapGet("/plugins/{plugin}", DetailAsync);
        routes.MapGet("/plugins/{plugin}/settings", SettingsAsync);
        routes.MapPut("/plugins/{plugin}/settings", SetSettingsAsync);
        routes.MapGet("/plugins/{plugin}/browse", BrowseAsync);
        routes.MapPost("/plugins/{plugin}/search", SearchAsync);
        routes.MapGet("/plugins/{plugin}/suggest", SuggestAsync);
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

    private static async Task<IResult> SettingsAsync(string plugin, EmmaRuntime runtime, CancellationToken cancellationToken)
    {
        Outcome<PluginId> id = RequestReader.Plugin(plugin);
        if (!id.IsSuccess)
        {
            return ApiResults.Failure(id.Error);
        }

        Outcome<IReadOnlyList<SettingState>> states = await runtime.GetPluginSettingsAsync(id.Value, cancellationToken).ConfigureAwait(false);
        return states.IsSuccess
            ? ApiResults.Ok(SettingsMapper.ToResponse(states.Value), ApiJsonContext.Default.PluginSettingsResponse)
            : ApiResults.Failure(states.Error);
    }

    private static async Task<IResult> SetSettingsAsync(
        string plugin,
        HttpContext context,
        EmmaRuntime runtime,
        CancellationToken cancellationToken)
    {
        Outcome<PluginId> id = RequestReader.Plugin(plugin);
        if (!id.IsSuccess)
        {
            return ApiResults.Failure(id.Error);
        }

        Outcome<SetSettingsRequest> body = await RequestReader.BodyAsync(context.Request, ApiJsonContext.Default.SetSettingsRequest, cancellationToken).ConfigureAwait(false);
        Outcome<IReadOnlyList<SettingChange>> changes = body.IsSuccess ? SettingsMapper.ToChanges(body.Value.Changes) : Outcome.Failure<IReadOnlyList<SettingChange>>(body.Error);
        if (!changes.IsSuccess)
        {
            return ApiResults.Failure(changes.Error);
        }

        Outcome<IReadOnlyList<SettingState>> states = await runtime.SetPluginSettingsAsync(id.Value, changes.Value, cancellationToken).ConfigureAwait(false);
        return states.IsSuccess
            ? ApiResults.Ok(SettingsMapper.ToResponse(states.Value), ApiJsonContext.Default.PluginSettingsResponse)
            : ApiResults.Failure(states.Error);
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

    private static async Task<IResult> SuggestAsync(
        string plugin,
        HttpContext context,
        CatalogPipeline catalog,
        CancellationToken cancellationToken)
    {
        Outcome<PluginId> id = RequestReader.Plugin(plugin);
        Outcome<string> filter = RequestReader.Required(context.Request, "filter");
        Outcome<string?> text = RequestReader.Optional(context.Request, "text");
        Outcome<int?> limit = RequestReader.Limit(context.Request, ContractLimits.MaxSuggestions);
        if (!id.IsSuccess || !filter.IsSuccess || !text.IsSuccess || !limit.IsSuccess)
        {
            return ApiResults.Failure((id.Error ?? filter.Error ?? text.Error ?? limit.Error)!);
        }

        Outcome<IReadOnlyList<Choice>> suggestions = await catalog
            .SuggestAsync(id.Value, filter.Value, text.Value ?? string.Empty, limit.Value ?? ContractLimits.DefaultSuggestions, cancellationToken)
            .ConfigureAwait(false);
        return suggestions.IsSuccess
            ? ApiResults.Ok(PluginMapper.ToSuggestions(suggestions.Value), ApiJsonContext.Default.SuggestionsResponse)
            : ApiResults.Failure(suggestions.Error);
    }
}
