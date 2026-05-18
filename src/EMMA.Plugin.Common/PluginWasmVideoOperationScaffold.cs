using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

/// <summary>
/// Provides helpers for implementing video-specific invoke operations.
/// </summary>
public static class PluginWasmVideoOperationScaffold
{
    /// <summary>
    /// Resolves and serializes the video streams available for a request.
    /// </summary>
    /// <param name="request">The operation request to resolve.</param>
    /// <param name="streamResolver">The callback that resolves stream data for a media identifier.</param>
    /// <param name="streamArrayTypeInfo">The JSON type metadata for the stream array.</param>
    /// <returns>A successful JSON operation result, or an invalid-arguments result when the request is incomplete.</returns>
    public static OperationResult InvokeVideoStreams<TStreamWire>(
        OperationRequest request,
        Func<string, TStreamWire[]> streamResolver,
        JsonTypeInfo<TStreamWire[]> streamArrayTypeInfo)
    {
        var mediaId = request.ResolveMediaId();
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return OperationResult.InvalidArguments("mediaId is required");
        }

        var streams = streamResolver(mediaId);
        var json = JsonSerializer.Serialize(streams, streamArrayTypeInfo);
        return PluginWasmInvokeScaffold.BuildJsonResult(json);
    }

    /// <summary>
    /// Resolves and serializes a single video segment for a request.
    /// </summary>
    /// <param name="request">The operation request to resolve.</param>
    /// <param name="segmentResolver">The callback that resolves a segment from media, stream, and sequence identifiers.</param>
    /// <param name="segmentTypeInfo">The JSON type metadata for the segment result.</param>
    /// <returns>A successful JSON operation result, or an invalid-arguments result when the request is incomplete.</returns>
    public static OperationResult InvokeVideoSegment<TSegmentWire>(
        OperationRequest request,
        Func<string, string, uint, TSegmentWire?> segmentResolver,
        JsonTypeInfo<TSegmentWire> segmentTypeInfo)
        where TSegmentWire : class
    {
        var mediaId = request.ResolveMediaId();
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return OperationResult.InvalidArguments("mediaId is required");
        }

        var streamId = PluginJsonArgs.GetString(request.argsJson, "streamId");
        if (string.IsNullOrWhiteSpace(streamId))
        {
            return OperationResult.InvalidArguments("streamId is required");
        }

        var sequence = PluginJsonArgs.GetInt32(request.argsJson, "sequence");
        if (sequence is null || sequence < 0)
        {
            return OperationResult.InvalidArguments("sequence must be a non-negative integer");
        }

        var segment = segmentResolver(mediaId, streamId, checked((uint)sequence.Value));
        if (segment is null)
        {
            return PluginWasmInvokeScaffold.BuildJsonResult("null");
        }

        var json = JsonSerializer.Serialize(segment, segmentTypeInfo);
        return PluginWasmInvokeScaffold.BuildJsonResult(json);
    }
}