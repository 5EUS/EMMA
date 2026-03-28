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

            var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
            if (error is not null || record is null)
            {
                return error is null
                    ? Results.Problem("Plugin resolution failed.")
                    : Results.Problem(detail: error.Message, statusCode: error.StatusCode);
            }

            if (wasmRuntimeHost.IsWasmPlugin(record.Manifest))
            {
                return Results.Problem(
                    detail: "Video pipeline endpoints are not wired for WASM plugins yet.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            if (address is null)
            {
                return Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            var correlationId = PluginGrpcHelpers.CreateCorrelationId();
            var endpoint = new PluginGrpcEndpoint(record, address, correlationId);
            var videoPort = new PluginVideoProviderPort(
                endpoint,
                options,
                loggerFactory.CreateLogger<PluginVideoProviderPort>());

            var streams = await videoPort.GetStreamsAsync(mediaId, cancellationToken);
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

            var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
            if (error is not null || record is null)
            {
                return error is null
                    ? Results.Problem("Plugin resolution failed.")
                    : Results.Problem(detail: error.Message, statusCode: error.StatusCode);
            }

            if (wasmRuntimeHost.IsWasmPlugin(record.Manifest))
            {
                return Results.Problem(
                    detail: "Video pipeline endpoints are not wired for WASM plugins yet.",
                    statusCode: StatusCodes.Status501NotImplemented);
            }

            if (address is null)
            {
                return Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            var correlationId = PluginGrpcHelpers.CreateCorrelationId();
            var endpoint = new PluginGrpcEndpoint(record, address, correlationId);
            var videoPort = new PluginVideoProviderPort(
                endpoint,
                options,
                loggerFactory.CreateLogger<PluginVideoProviderPort>());

            var segment = await videoPort.GetSegmentAsync(mediaId, streamId, sequence ?? 0, cancellationToken);
            return Results.File(segment.Payload, segment.ContentType);
        });

        return app;
    }
}
