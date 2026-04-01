using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

public static class PluginWasmVideoOperationScaffold
{
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