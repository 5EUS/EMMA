using EMMA.Contracts.Plugins;
using EMMA.PluginHost.Plugins;
using Grpc.Net.Client;
using System.Net;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Temporary HTTP probe endpoints for manual validation.
/// TODO: Deprecate once the public API layer is implemented.
/// </summary>
public static class ProbeEndpoints
{
    public static WebApplication MapProbeEndpoints(this WebApplication app)
    {
        app.MapGet("/probe/search", async (
            string? query,
            string? pluginId,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var (record, address, error) = TryResolvePlugin(registry, pluginId);
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            try
            {
                using var httpClient = CreateHttpClient(address);
                using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    HttpClient = httpClient
                });

                var client = new SearchProvider.SearchProviderClient(channel);
                var response = await client.SearchAsync(new SearchRequest
                {
                    Query = query ?? string.Empty
                }, cancellationToken: cancellationToken);

                var results = response.Results.Select(result => new
                {
                    result.Id,
                    result.Source,
                    result.Title,
                    result.MediaType
                });

                return Results.Ok(new
                {
                    PluginId = record.Manifest.Id,
                    Query = query ?? string.Empty,
                    Count = response.Results.Count,
                    Results = results
                });
            }
            catch (Exception ex)
            {
                return HandleProbeException(ex);
            }
        });

        app.MapGet("/probe/pages", async (
            string? mediaId,
            string? chapterId,
            int? index,
            string? pluginId,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var (record, address, error) = TryResolvePlugin(registry, pluginId);
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            if (string.IsNullOrWhiteSpace(mediaId))
            {
                return Results.BadRequest(new { message = "mediaId is required." });
            }

            var pageIndex = index ?? 0;

            try
            {
                using var httpClient = CreateHttpClient(address);
                using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    HttpClient = httpClient
                });

                var client = new PageProvider.PageProviderClient(channel);
                var chapters = await client.GetChaptersAsync(new ChaptersRequest
                {
                    MediaId = mediaId
                }, cancellationToken: cancellationToken);

                MediaPage? page = null;
                if (!string.IsNullOrWhiteSpace(chapterId))
                {
                    var pageResponse = await client.GetPageAsync(new PageRequest
                    {
                        MediaId = mediaId,
                        ChapterId = chapterId,
                        Index = pageIndex
                    }, cancellationToken: cancellationToken);

                    page = pageResponse.Page;
                }

                var chapterResults = chapters.Chapters.Select(chapter => new
                {
                    chapter.Id,
                    chapter.Number,
                    chapter.Title
                });

                var pageResult = page is null
                    ? null
                    : new
                    {
                        page.Id,
                        page.Index,
                        page.ContentUri
                    };

                return Results.Ok(new
                {
                    PluginId = record.Manifest.Id,
                    MediaId = mediaId,
                    Chapters = chapterResults,
                    Page = pageResult
                });
            }
            catch (Exception ex)
            {
                return HandleProbeException(ex);
            }
        });

        app.MapGet("/probe/video", async (
            string? mediaId,
            string? streamId,
            int? sequence,
            string? pluginId,
            PluginRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var (record, address, error) = TryResolvePlugin(registry, pluginId);
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            if (string.IsNullOrWhiteSpace(mediaId))
            {
                return Results.BadRequest(new { message = "mediaId is required." });
            }

            var segmentSequence = sequence ?? 0;

            try
            {
                using var httpClient = CreateHttpClient(address);
                using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
                {
                    HttpClient = httpClient
                });

                var client = new VideoProvider.VideoProviderClient(channel);
                var streams = await client.GetStreamsAsync(new StreamRequest
                {
                    MediaId = mediaId
                }, cancellationToken: cancellationToken);

                SegmentResponse? segment = null;
                if (!string.IsNullOrWhiteSpace(streamId))
                {
                    segment = await client.GetSegmentAsync(new SegmentRequest
                    {
                        MediaId = mediaId,
                        StreamId = streamId,
                        Sequence = segmentSequence
                    }, cancellationToken: cancellationToken);
                }

                var streamResults = streams.Streams.Select(stream => new
                {
                    stream.Id,
                    stream.Label,
                    stream.PlaylistUri
                });

                object? segmentResult = null;
                if (segment is not null)
                {
                    var payload = segment.Payload?.ToByteArray() ?? [];
                    segmentResult = new
                    {
                        segment.ContentType,
                        Size = payload.Length,
                        PayloadBase64 = payload.Length == 0 ? null : Convert.ToBase64String(payload)
                    };
                }

                return Results.Ok(new
                {
                    PluginId = record.Manifest.Id,
                    MediaId = mediaId,
                    Streams = streamResults,
                    Segment = segmentResult
                });
            }
            catch (Exception ex)
            {
                return HandleProbeException(ex);
            }
        });

        return app;
    }

    private static PluginRecord? ResolvePluginRecord(PluginRegistry registry, string? pluginId)
    {
        var snapshot = registry.GetSnapshot();
        if (snapshot.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return snapshot[0];
        }

        return snapshot.FirstOrDefault(record =>
            string.Equals(record.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
    }

    private static (PluginRecord? Record, Uri? Address, IResult? Error) TryResolvePlugin(
        PluginRegistry registry,
        string? pluginId)
    {
        var record = ResolvePluginRecord(registry, pluginId);
        if (record is null)
        {
            return (null, null, Results.NotFound(new { message = "No matching plugin record found." }));
        }

        if (record.Manifest.Entry is null)
        {
            return (record, null, Results.Problem("Plugin manifest has no entry."));
        }

        if (!string.Equals(record.Manifest.Entry.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return (record, null, Results.Problem($"Unsupported plugin protocol: {record.Manifest.Entry.Protocol}."));
        }

        if (string.IsNullOrWhiteSpace(record.Manifest.Entry.Endpoint))
        {
            return (record, null, Results.Problem("Plugin manifest entry is missing endpoint."));
        }

        if (!Uri.TryCreate(record.Manifest.Entry.Endpoint, UriKind.Absolute, out var address))
        {
            return (record, null, Results.Problem("Plugin manifest entry endpoint is invalid."));
        }

        return (record, address, null);
    }

    private static HttpClient CreateHttpClient(Uri address)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = address,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        return httpClient;
    }

    private static IResult HandleProbeException(Exception ex)
    {
        return ex is Grpc.Core.RpcException rpcEx
            ? Results.Problem($"gRPC call failed: {rpcEx.Status.Detail}")
            : Results.Problem($"Probe failed: {ex.Message}");
    }
}
