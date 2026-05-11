using EMMA.Contracts.Plugins;
using EMMA.Plugin.AspNetCore;
using EMMA.Plugin.Common;
using EMMA.TemplatePlugin.Core;
using Microsoft.Extensions.Logging;

namespace EMMA.TemplatePlugin.ASPNET;

public sealed class AspNetClient(ILogger<AspNetClient> logger) : IPluginPagedMediaRuntime
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

        _logger.LogInformation("Template search query={Query} results={Count}", query, results.Count);
        return Task.FromResult<IReadOnlyList<MediaSummary>>(results);
    }

    public Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(string mediaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var items = Core.GetChapters(mediaId);
        var results = PluginTypedExportScaffold.MapList(
            items,
            item => new MediaChapter
            {
                Id = item.id,
                Number = item.number,
                Title = item.title,
            });

        return Task.FromResult<IReadOnlyList<MediaChapter>>(results);
    }

    public Task<MediaPage?> GetPageAsync(string chapterId, int pageIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var page = Core.GetPage(chapterId, pageIndex);
        return Task.FromResult<MediaPage?>(page is null
            ? null
            : new MediaPage
            {
                Id = page.id,
                Index = page.index,
                ContentUri = page.contentUri,
            });
    }

    public Task<(IReadOnlyList<MediaPage> Pages, bool ReachedEnd)> GetPagesAsync(
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pages = Core.GetPages(chapterId, startIndex, count);
        var results = PluginTypedExportScaffold.MapList(
            pages,
            item => new MediaPage
            {
                Id = item.id,
                Index = item.index,
                ContentUri = item.contentUri,
            });

        var reachedEnd = startIndex + results.Count >= Core.GetPageCount(chapterId);
        return Task.FromResult((Pages: (IReadOnlyList<MediaPage>)results, ReachedEnd: reachedEnd));
    }
}