using EMMA.Contracts.Plugins;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Services;

public sealed class VideoProviderService(ILogger<VideoProviderService> logger) : VideoProvider.VideoProviderBase
{
    private readonly ILogger<VideoProviderService> _logger = logger;

    public override Task<StreamResponse> GetStreams(StreamRequest request, ServerCallContext context)
    {
        PluginRpcGuard.EnsureActive(context);
        var correlationId = PluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation("Streams request {CorrelationId} mediaId={MediaId}", correlationId, request.MediaId);

        var response = new StreamResponse();

        return Task.FromResult(response);
    }

    public override Task<SegmentResponse> GetSegment(SegmentRequest request, ServerCallContext context)
    {
        PluginRpcGuard.EnsureActive(context);
        var correlationId = PluginRpcGuard.GetCorrelationId(context, request.Context?.CorrelationId);

        _logger.LogInformation(
            "Segment request {CorrelationId} mediaId={MediaId} streamId={StreamId} sequence={Sequence}",
            correlationId,
            request.MediaId,
            request.StreamId,
            request.Sequence);

        return Task.FromResult(new SegmentResponse());
    }
}
