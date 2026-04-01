using Google.Protobuf;
using EMMA.Plugin.Common;
using EMMA.Plugin.AspNetCore;
using EMMA.Contracts.Plugins;
using EMMA.PluginTemplate.Infrastructure;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginTemplate.Services;

/// <summary>
/// Default runtime skeleton for plugin transport adapters.
/// </summary>
public sealed class AspNetClient(ILogger<AspNetClient> logger)
    : IPluginPagedMediaRuntime, IPluginVideoRuntime
{
    private static readonly CoreClient Core = new();

    private readonly ILogger<AspNetClient> _logger = logger;

    public Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = Core.Search(query);
        var results = PluginTypedExportScaffold.MapList(
            items,
            item => new MediaSummary
            {
                Id = item.id,
                Source = item.source,
                Title = item.title,
                MediaType = item.mediaType,
                ThumbnailUrl = item.thumbnailUrl ?? string.Empty,
                Description = item.description ?? string.Empty,
            });

        _logger.LogInformation("Plugin search query={Query} results={Count}", query, results.Count);
        return Task.FromResult<IReadOnlyList<MediaSummary>>(results);
    }

    public Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var chapters = Core.GetChapters(mediaId);
        var results = PluginTypedExportScaffold.MapList(
            chapters,
            chapter => new MediaChapter
            {
                Id = chapter.id,
                Number = chapter.number,
                Title = chapter.title,
            });

        return Task.FromResult<IReadOnlyList<MediaChapter>>(results);
    }

    public Task<MediaPage?> GetPageAsync(string chapterId, int pageIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<MediaPage?>(null);
    }

    public Task<(IReadOnlyList<MediaPage> Pages, bool ReachedEnd)> GetPagesAsync(
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((Pages: (IReadOnlyList<MediaPage>)[], ReachedEnd: true));
    }

    public Task<StreamResponse> GetStreamsAsync(string mediaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var response = new StreamResponse();
        var streams = Core.GetStreams(mediaId);
        if (streams.Count > 0)
        {
            response.Streams.AddRange(streams.Select(stream => new StreamInfo
            {
                Id = stream.Id,
                Label = stream.Label,
                PlaylistUri = stream.PlaylistUri,
            }));
        }

        _logger.LogInformation("Plugin streams mediaId={MediaId} count={Count}", mediaId, response.Streams.Count);
        return Task.FromResult(response);
    }

    public Task<SegmentResponse> GetSegmentAsync(string mediaId, string streamId, int sequence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (sequence < 0)
        {
            return Task.FromResult(new SegmentResponse());
        }

        var segment = Core.GetSegment(mediaId, streamId, checked((uint)sequence));
        if (segment is null)
        {
            return Task.FromResult(new SegmentResponse());
        }

        return Task.FromResult(new SegmentResponse
        {
            ContentType = segment.Value.ContentType,
            Payload = ByteString.CopyFrom(segment.Value.Payload),
        });
    }
}
