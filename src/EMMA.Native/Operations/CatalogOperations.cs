using EMMA.Api.Contracts;
using EMMA.Api.Mapping;
using EMMA.Domain;
using EMMA.Native.Requests;

namespace EMMA.Native.Operations;

/// <summary>Search, browsing and lookup. Every media type goes through these.</summary>
internal static class CatalogOperations
{
    /// <summary>Searches one plugin.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SearchAsync(OperationContext context)
    {
        Outcome<SearchCall> call = context.Read(NativeJsonContext.Default.SearchCall);
        Outcome<PluginId> plugin = call.IsSuccess ? RequestParsing.Plugin(call.Value.Plugin) : Outcome.Failure<PluginId>(call.Error);
        if (!plugin.IsSuccess)
        {
            return Envelope.Failure(plugin.Error);
        }

        Outcome<Query> query = NodeMapper.ToQuery(call.Value.Query ?? new SearchRequest(null, null, null, null, null, null));
        if (!query.IsSuccess)
        {
            return Envelope.Failure(query.Error);
        }

        return Page(await context.Runtime.Catalog.SearchAsync(plugin.Value, query.Value, context.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists a plugin's browse root.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> BrowseAsync(OperationContext context)
    {
        Outcome<PageCall> call = context.Read(NativeJsonContext.Default.PageCall);
        Outcome<PluginId> plugin = call.IsSuccess ? RequestParsing.Plugin(call.Value.Plugin) : Outcome.Failure<PluginId>(call.Error);
        Outcome<PageRequest> page = call.IsSuccess ? RequestParsing.Page(call.Value.Cursor, call.Value.Limit) : Outcome.Failure<PageRequest>(call.Error);
        if (!plugin.IsSuccess || !page.IsSuccess)
        {
            return Envelope.Failure((plugin.Error ?? page.Error)!);
        }

        return Page(await context.Runtime.Catalog.BrowseRootAsync(plugin.Value, page.Value, context.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lists the children of a node.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> ChildrenAsync(OperationContext context)
    {
        Outcome<PageCall> call = context.Read(NativeJsonContext.Default.PageCall);
        Outcome<NodeRef> parent = call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
        Outcome<PageRequest> page = call.IsSuccess ? RequestParsing.Page(call.Value.Cursor, call.Value.Limit) : Outcome.Failure<PageRequest>(call.Error);
        if (!parent.IsSuccess || !page.IsSuccess)
        {
            return Envelope.Failure((parent.Error ?? page.Error)!);
        }

        return Page(await context.Runtime.Catalog.ChildrenAsync(parent.Value, page.Value, context.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>Fetches nodes by reference.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> GetAsync(OperationContext context)
    {
        Outcome<RefsCall> call = context.Read(NativeJsonContext.Default.RefsCall);
        Outcome<IReadOnlyList<NodeRef>> references = call.IsSuccess ? RequestParsing.Nodes(call.Value.Refs, "refs") : Outcome.Failure<IReadOnlyList<NodeRef>>(call.Error);
        if (!references.IsSuccess)
        {
            return Envelope.Failure(references.Error);
        }

        Outcome<IReadOnlyList<Node>> nodes = await context.Runtime.Catalog.GetAsync(references.Value, context.CancellationToken).ConfigureAwait(false);
        return nodes.IsSuccess
            ? Envelope.Ok(new NodeListResponse([.. nodes.Value.Select(NodeMapper.ToDto)]), ApiJsonContext.Default.NodeListResponse)
            : Envelope.Failure(nodes.Error);
    }

    /// <summary>Suggests values for a plugin's lookup filter.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> SuggestAsync(OperationContext context)
    {
        Outcome<SuggestCall> call = context.Read(NativeJsonContext.Default.SuggestCall);
        Outcome<PluginId> plugin = call.IsSuccess ? RequestParsing.Plugin(call.Value.Plugin) : Outcome.Failure<PluginId>(call.Error);
        Outcome<(string Filter, string Text, int Limit)> arguments = call.IsSuccess
            ? RequestParsing.Suggestion(call.Value.Filter, call.Value.Text, call.Value.Limit)
            : Outcome.Failure<(string, string, int)>(call.Error);
        if (!plugin.IsSuccess || !arguments.IsSuccess)
        {
            return Envelope.Failure((plugin.Error ?? arguments.Error)!);
        }

        Outcome<IReadOnlyList<Choice>> suggestions = await context.Runtime.Catalog
            .SuggestAsync(plugin.Value, arguments.Value.Filter, arguments.Value.Text, arguments.Value.Limit, context.CancellationToken)
            .ConfigureAwait(false);
        return suggestions.IsSuccess
            ? Envelope.Ok(PluginMapper.ToSuggestions(suggestions.Value), ApiJsonContext.Default.SuggestionsResponse)
            : Envelope.Failure(suggestions.Error);
    }

    /// <summary>Fills in expensive props of nodes the host has already seen.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> EnrichAsync(OperationContext context)
    {
        Outcome<RefsCall> call = context.Read(NativeJsonContext.Default.RefsCall);
        Outcome<IReadOnlyList<NodeRef>> references = call.IsSuccess ? RequestParsing.Nodes(call.Value.Refs, "refs") : Outcome.Failure<IReadOnlyList<NodeRef>>(call.Error);
        if (!references.IsSuccess)
        {
            return Envelope.Failure(references.Error);
        }

        Outcome<IReadOnlyList<Node>> nodes = await context.Runtime.Catalog.EnrichAsync(references.Value, context.CancellationToken).ConfigureAwait(false);
        return nodes.IsSuccess
            ? Envelope.Ok(new NodeListResponse([.. nodes.Value.Select(NodeMapper.ToDto)]), ApiJsonContext.Default.NodeListResponse)
            : Envelope.Failure(nodes.Error);
    }

    /// <summary>Asks the plugin for a node again, so its thumbnail and details are current.</summary>
    /// <param name="context">The call.</param>
    /// <returns>The response.</returns>
    public static async Task<NativeResponse> RefreshCoverAsync(OperationContext context)
    {
        Outcome<RefCall> call = context.Read(NativeJsonContext.Default.RefCall);
        Outcome<NodeRef> node = call.IsSuccess ? RequestParsing.Node(call.Value.Ref, "ref") : Outcome.Failure<NodeRef>(call.Error);
        if (!node.IsSuccess)
        {
            return Envelope.Failure(node.Error);
        }

        Outcome<Node> refreshed = await context.Runtime.Refresh.RefreshNodeAsync(node.Value, context.CancellationToken).ConfigureAwait(false);
        return refreshed.IsSuccess
            ? Envelope.Ok(NodeMapper.ToDto(refreshed.Value), ApiJsonContext.Default.NodeDto)
            : Envelope.Failure(refreshed.Error);
    }

    private static NativeResponse Page(Outcome<NodePage> page) =>
        page.IsSuccess
            ? Envelope.Ok(NodeMapper.ToDto(page.Value), ApiJsonContext.Default.NodePageResponse)
            : Envelope.Failure(page.Error);
}
