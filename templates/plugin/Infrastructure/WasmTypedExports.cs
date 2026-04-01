#if PLUGIN_TRANSPORT_WASM
using System.Text.Json;
using EMMA.Plugin.Common;
using EMMA.PluginTemplate.Infrastructure;
using LibraryWorld;
using LibraryWorld.wit.exports.emma.plugin;
using LibraryWorld.wit.imports.emma.plugin;

namespace LibraryWorld.wit.exports.emma.plugin;

/// <summary>
/// WIT export bridge that adapts typed component calls to template program handlers.
/// </summary>
public static class PluginImpl
{
    private static readonly PluginOperationPayloadRouter InvokePayloadRouter = BuildInvokePayloadRouter();
    private static readonly CoreClient Core = new();

    public static IPlugin.HandshakeResponse Handshake()
    {
        var handshake = EMMA.PluginTemplate.Program.handshake();
        return new IPlugin.HandshakeResponse(handshake.version, handshake.message);
    }

    public static List<IPlugin.Capability> Capabilities()
    {
        var capabilities = EMMA.PluginTemplate.Program.capabilities();
        return PluginTypedExportScaffold.MapList(
            capabilities,
            capability => new IPlugin.Capability(
                capability.name,
                [.. capability.mediaTypes],
                [.. capability.operations]));
    }

    public static List<IPlugin.MediaSearchItem> Search(string query, string payloadJson)
    {
        payloadJson ??= string.Empty;
        var items = EMMA.PluginTemplate.Program.search(query, payloadJson);

        return PluginTypedExportScaffold.MapList(
            items,
            item => new IPlugin.MediaSearchItem(
                item.id,
                item.source,
                item.title,
                item.mediaType,
                item.thumbnailUrl,
                item.description,
                []));
    }

    public static List<IPlugin.ChapterItem> Chapters(string mediaId, string payloadJson)
    {
        payloadJson ??= string.Empty;
        var items = EMMA.PluginTemplate.Program.chapters(mediaId, payloadJson);

        return PluginTypedExportScaffold.MapList(
            items,
            item => new IPlugin.ChapterItem(
                item.id,
                checked((uint)item.number),
                item.title,
                [.. item.uploaderGroups ?? []]));
    }

    public static IPlugin.PageItem? Page(string mediaId, string chapterId, uint pageIndex, string payloadJson)
    {
        payloadJson = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            "page",
            ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId),
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint));

        var page = EMMA.PluginTemplate.Program.page(mediaId, chapterId, pageIndex, payloadJson);
        return PluginTypedExportScaffold.MapNullable(
            page,
            value => new IPlugin.PageItem(value.id, checked((uint)value.index), value.contentUri));
    }

    public static List<IPlugin.PageItem> Pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson)
    {
        payloadJson = PluginPayloadResolvers.ResolveProvidedOrHostPayload(
            payloadJson,
            "pages",
            ProviderRequestUrls.BuildAtHomeAbsoluteUrl(chapterId),
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint));

        var pages = EMMA.PluginTemplate.Program.pages(mediaId, chapterId, startIndex, count, payloadJson);
        return PluginTypedExportScaffold.MapList(
            pages,
            page => new IPlugin.PageItem(page.id, checked((uint)page.index), page.contentUri));
    }

    public static IPlugin.MediaOperationResponse Invoke(IPlugin.MediaOperationRequest request)
    {
        var payload = ResolveInvokePayload(request);

        var result = EMMA.PluginTemplate.Program.invoke(new OperationRequest(
            request.operation,
            request.mediaId,
            request.mediaType,
            request.argsJson,
            payload));

        if (result.isError)
        {
            throw CreateOperationError(result.error);
        }

        return new IPlugin.MediaOperationResponse(result.contentType, result.payloadJson);
    }

    public static List<IPlugin.VideoStreamItem> VideoStreams(string mediaId, string payloadJson)
    {
        var streams = Core.GetStreams(mediaId);
        if (streams.Count == 0)
        {
            return [];
        }

        return PluginTypedExportScaffold.MapList(
            streams,
            stream => new IPlugin.VideoStreamItem(stream.Id, stream.Label, stream.PlaylistUri));
    }

    public static IPlugin.VideoSegmentItem? VideoSegment(string mediaId, string streamId, uint sequence, string payloadJson)
    {
        var segment = Core.GetSegment(mediaId, streamId, sequence);
        if (segment is null)
        {
            return null;
        }

        return new IPlugin.VideoSegmentItem(segment.Value.ContentType, segment.Value.Payload);
    }

    private static string? ResolveInvokePayload(IPlugin.MediaOperationRequest request)
    {
        return PluginTypedExportScaffold.ResolveInvokePayload(
            request.operation,
            request.mediaId,
            request.mediaType,
            request.argsJson,
            request.payloadJson,
            InvokePayloadRouter,
            (operation, hint) => HostBridgeInterop.OperationPayload(operation, hint),
            useArgsJsonFallbackHint: true);
    }

    private static WitException<IPlugin.OperationError> CreateOperationError(string? error)
    {
        var parsed = PluginTypedExportScaffold.ResolveOperationError(error);

        return parsed.Kind switch
        {
            PluginOperationErrorKind.UnsupportedOperation => new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.UnsupportedOperation(parsed.Message),
                0),
            PluginOperationErrorKind.InvalidArguments => new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.InvalidArguments(parsed.Message),
                0),
            _ => new WitException<IPlugin.OperationError>(
                IPlugin.OperationError.Failed(parsed.Message),
                0)
        };
    }

    private static PluginOperationPayloadRouter BuildInvokePayloadRouter()
    {
        return new PluginOperationPayloadRouter()
            .Register("search", request => ProviderRequestUrls.BuildSearchAbsoluteUrl(PluginSearchQuery.Parse(request.argsJson)))
            .Register("benchmark-network", request => ProviderRequestUrls.BuildSearchAbsoluteUrl(PluginSearchQuery.Parse(request.argsJson)))
            .Register("chapters", request => ProviderRequestUrls.BuildChaptersAbsoluteUrl(request.ResolveMediaId()))
            .Register("page", request => ProviderRequestUrls.BuildAtHomeAbsoluteUrl(request.ResolveChapterId()))
            .Register("pages", request => ProviderRequestUrls.BuildAtHomeAbsoluteUrl(request.ResolveChapterId()));
    }
}
#endif
