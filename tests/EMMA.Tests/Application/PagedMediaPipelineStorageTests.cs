using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using EMMA.Storage;

namespace EMMA.Tests.Application;

public sealed class PagedMediaPipelineStorageTests
{
    [Fact]
    public async Task PipelinePersistsSearchChaptersAndPages()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "emma-tests", Guid.NewGuid().ToString("N"), "emma.db");
        Directory.CreateDirectory(Path.GetDirectoryName(tempDb)!);

        var options = new StorageOptions(tempDb);
        var initializer = new StorageInitializer(options);
        await initializer.InitializeAsync(CancellationToken.None);

        var catalog = new SqliteMediaCatalogPort(options);

        var cache = new InMemoryCachePort();
        var policy = new HostPolicyEvaluator();
        var search = new StubSearchPort();
        var pages = new StubPageProviderPort();

        var pipeline = new PagedMediaPipeline(
            search,
            pages,
            policy,
            cache,
            catalog: catalog);

        var results = await pipeline.SearchAsync("demo", CancellationToken.None);
        Assert.Single(results);

        var mediaId = results[0].Id;
        var chapters = await pipeline.GetChaptersAsync(mediaId, CancellationToken.None);
        Assert.Single(chapters);

        var page = await pipeline.GetPageAsync(mediaId, chapters[0].ChapterId, 0, CancellationToken.None);
        Assert.Equal("page-1", page.PageId);

        var stored = await catalog.GetMediaAsync(mediaId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("Demo Title", stored!.Title);

        var storedChapters = await catalog.GetChaptersAsync(mediaId, CancellationToken.None);
        Assert.Single(storedChapters);
        Assert.Equal("ch-1", storedChapters[0].ChapterId);

        var storedPages = await catalog.GetPagesAsync(mediaId, "ch-1", CancellationToken.None);
        Assert.Single(storedPages);
        Assert.Equal("page-1", storedPages[0].PageId);
    }

    private sealed class StubSearchPort : IMediaSearchPort
    {
        public Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            var summary = new MediaSummary(MediaId.Create("demo-1"), "test", "Demo Title", MediaType.Paged);
            return Task.FromResult<IReadOnlyList<MediaSummary>>([summary]);
        }
    }

    private sealed class StubPageProviderPort : IPageProviderPort
    {
        public Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<MediaChapter>>([new MediaChapter("ch-1", 1, "Chapter One")]);
        }

        public Task<MediaPage> GetPageAsync(
            MediaId mediaId,
            string chapterId,
            int pageIndex,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new MediaPage("page-1", pageIndex, new Uri("https://example.invalid/page-1.jpg")));
        }

        public Task<MediaPagesResult> GetPagesAsync(
            MediaId mediaId,
            string chapterId,
            int startIndex,
            int count,
            CancellationToken cancellationToken)
        {
            var pages = Enumerable.Range(startIndex, Math.Max(0, count))
                .Select(index => new MediaPage($"page-{index}", index, new Uri($"https://example.invalid/page-{index}.jpg")))
                .ToList();
            return Task.FromResult(new MediaPagesResult(pages, true));
        }
    }
}
