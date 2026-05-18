using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

/// <summary>
/// Extends <see cref="PluginBasicPagedWasmOperationHost{TChapterOperationItem}"/> with standard video invoke and CLI operations.
/// </summary>
/// <typeparam name="TChapterOperationItem">The serialized chapter-operation item type used for invoke responses.</typeparam>
public abstract class PluginBasicPagedVideoWasmOperationHost<TChapterOperationItem>
    : PluginBasicPagedWasmOperationHost<TChapterOperationItem>
{
    /// <summary>
    /// Initializes the standard paged + video WASM host with the supplied paged host options.
    /// </summary>
    /// <param name="options">The paged host options describing the standard operation responses and JSON type metadata.</param>
    protected PluginBasicPagedVideoWasmOperationHost(PluginBasicPagedWasmHostOptions<TChapterOperationItem> options)
        : base(options)
    {
    }

    /// <summary>
    /// The JSON type metadata for <see cref="VideoStreamOperationItem"/> arrays.
    /// </summary>
    protected abstract JsonTypeInfo<VideoStreamOperationItem[]> VideoStreamArrayTypeInfo { get; }

    /// <summary>
    /// The JSON type metadata for <see cref="VideoSegmentOperationItem"/>.
    /// </summary>
    protected abstract JsonTypeInfo<VideoSegmentOperationItem> VideoSegmentTypeInfo { get; }

    /// <summary>
    /// Resolves the standard video stream operation items for a media identifier.
    /// </summary>
    /// <param name="mediaId">The media identifier whose streams should be returned.</param>
    /// <returns>The resolved stream items.</returns>
    protected abstract IReadOnlyList<VideoStreamOperationItem> GetVideoStreams(string mediaId);

    /// <summary>
    /// Resolves a single standard video segment operation item.
    /// </summary>
    /// <param name="mediaId">The media identifier associated with the stream.</param>
    /// <param name="streamId">The stream identifier.</param>
    /// <param name="sequence">The segment sequence number.</param>
    /// <returns>The resolved segment item, or <see langword="null"/> when the segment cannot be produced.</returns>
    protected abstract VideoSegmentOperationItem? GetVideoSegment(string mediaId, string streamId, uint sequence);

    /// <summary>
    /// Determines whether the current request supports video operations.
    /// </summary>
    /// <param name="request">The current operation request.</param>
    /// <returns><see langword="true"/> when the request should be treated as video-capable.</returns>
    protected virtual bool SupportsVideoRequests(OperationRequest request)
    {
        return request.IsVideoMediaRequest();
    }

    /// <summary>
    /// Allows derived hosts to register additional CLI handlers after the standard video CLI operations.
    /// </summary>
    /// <param name="builder">The WASM host builder being configured.</param>
    protected virtual void ConfigureAdditionalCliHandlers(PluginWasmHostBuilder builder)
    {
    }

    /// <summary>
    /// Allows derived hosts to register or replace invoke handlers after the standard video invoke operations.
    /// </summary>
    /// <param name="dispatcher">The dispatcher preconfigured with the standard paged + video handlers.</param>
    /// <returns>The dispatcher instance that should be used by the host.</returns>
    protected virtual PluginOperationDispatcher ConfigureAdditionalInvokeHandlers(PluginOperationDispatcher dispatcher)
    {
        return dispatcher;
    }

    /// <inheritdoc />
    protected override void ConfigureCustomCliHandlers(PluginWasmHostBuilder builder)
    {
        base.ConfigureCustomCliHandlers(builder);

        builder
            .AddCliJson(
                PluginOperationNames.VideoStreams,
                (args, _) => VideoStreamsForCli(args),
                VideoStreamArrayTypeInfo)
            .AddCliHandler(
                PluginOperationNames.VideoSegment,
                SerializeVideoSegmentForCli);

        ConfigureAdditionalCliHandlers(builder);
    }

    /// <inheritdoc />
    protected override PluginOperationDispatcher ConfigureCustomInvokeHandlers(PluginOperationDispatcher dispatcher)
    {
        dispatcher = base.ConfigureCustomInvokeHandlers(dispatcher)
            .RegisterVideoOperations(InvokeVideoStreams, InvokeVideoSegment, SupportsVideoRequests);

        return ConfigureAdditionalInvokeHandlers(dispatcher);
    }

    /// <inheritdoc />
    protected override bool SupportsChapterRequests(OperationRequest request)
    {
        return request.IsPagedMediaRequest() || request.IsVideoMediaRequest();
    }

    private VideoStreamOperationItem[] VideoStreamsForCli(string[] args)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            return [];
        }

        return [.. GetVideoStreams(args[0])];
    }

    private string SerializeVideoSegmentForCli(string[] args, string _)
    {
        if (args.Length < 3
            || string.IsNullOrWhiteSpace(args[0])
            || string.IsNullOrWhiteSpace(args[1])
            || !uint.TryParse(args[2], out var sequence))
        {
            return "null";
        }

        var segment = GetVideoSegment(args[0], args[1], sequence);
        return segment is null
            ? "null"
            : JsonSerializer.Serialize(segment, VideoSegmentTypeInfo);
    }

    private OperationResult InvokeVideoStreams(OperationRequest request)
    {
        return PluginWasmVideoOperationScaffold.InvokeVideoStreams(
            request,
            mediaId => [.. GetVideoStreams(mediaId)],
            VideoStreamArrayTypeInfo);
    }

    private OperationResult InvokeVideoSegment(OperationRequest request)
    {
        return PluginWasmVideoOperationScaffold.InvokeVideoSegment(
            request,
            GetVideoSegment,
            VideoSegmentTypeInfo);
    }
}