using EMMA.Domain;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Video pipeline endpoints backed by plugin gRPC ports.
/// </summary>
public static class VideoPipelineEndpoints
{
    public static WebApplication MapVideoPipelineEndpoints(this WebApplication app)
    {
        app.MapGet("/pipeline/video/streams", async (
            string? mediaId,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                return Results.BadRequest(new { message = "mediaId is required." });
            }

            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            IReadOnlyList<VideoStreamResult> streams;
            if (IsWasm)
            {
                var wasmStreams = await wasmRuntimeHost.GetVideoStreamsAsync(
                    record,
                    MediaId.Create(mediaId),
                    cancellationToken);

                streams = [.. wasmStreams
                    .Select(stream => new VideoStreamResult(
                        stream.Id ?? string.Empty,
                        stream.Label ?? string.Empty,
                        stream.PlaylistUri ?? string.Empty))];
            }
            else
            {
                var videoPort = CreateVideoPort(record, Address!, options, loggerFactory);

                streams = await videoPort.GetStreamsAsync(mediaId, cancellationToken);
            }

            return Results.Ok(new
            {
                Streams = streams.Select(stream => new
                {
                    stream.Id,
                    stream.Label,
                    stream.PlaylistUri
                })
            });
        });

        app.MapGet("/pipeline/video/segment", async (
            string? mediaId,
            string? streamId,
            int? sequence,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(streamId))
            {
                return Results.BadRequest(new { message = "mediaId and streamId are required." });
            }

            if ((sequence ?? 0) < 0)
            {
                return Results.BadRequest(new { message = "sequence must be >= 0." });
            }

            var (Record, Address, IsWasm, Error) = await ResolvePluginAsync(pluginId, pluginResolution, wasmRuntimeHost, cancellationToken);
            if (Error is not null)
            {
                return Error;
            }

            var record = Record!;

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            VideoSegmentResult segment;
            if (IsWasm)
            {
                var wasmSegment = await wasmRuntimeHost.GetVideoSegmentAsync(
                    record,
                    MediaId.Create(mediaId),
                    streamId,
                    sequence ?? 0,
                    cancellationToken);

                if (wasmSegment is null)
                {
                    return Results.NotFound(new
                    {
                        message = "Video segment not found."
                    });
                }

                segment = new VideoSegmentResult(
                    wasmSegment.ContentType,
                    wasmSegment.Payload);
            }
            else
            {
                var videoPort = CreateVideoPort(record, Address!, options, loggerFactory);

                segment = await videoPort.GetSegmentAsync(mediaId, streamId, sequence ?? 0, cancellationToken);
            }

            return Results.File(segment.Payload, segment.ContentType);
        });

        return app;
    }

    private static PluginVideoProviderPort CreateVideoPort(
        PluginRecord record,
        Uri address,
        IOptions<PluginHostOptions> options,
        ILoggerFactory loggerFactory)
    {
        var correlationId = PluginGrpcHelpers.CreateCorrelationId();
        var endpoint = new PluginGrpcEndpoint(record, address, correlationId);
        return new PluginVideoProviderPort(
            endpoint,
            options,
            loggerFactory.CreateLogger<PluginVideoProviderPort>());
    }

    private static async ValueTask<(PluginRecord? Record, Uri? Address, bool IsWasm, IResult? Error)> ResolvePluginAsync(
        string? pluginId,
        PluginResolutionService pluginResolution,
        IWasmPluginRuntimeHost wasmRuntimeHost,
        CancellationToken cancellationToken)
    {
        var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
        if (error is not null || record is null)
        {
            var result = error is null
                ? Results.Problem("Plugin resolution failed.")
                : Results.Problem(detail: error.Message, statusCode: error.StatusCode);

            return (null, null, false, result);
        }

        var isWasm = wasmRuntimeHost.IsWasmPlugin(record.Manifest);
        if (!isWasm && address is null)
        {
            return (null, null, false, Results.Problem("Plugin resolution failed."));
        }

        return (record, address, isWasm, null);
    }
}
