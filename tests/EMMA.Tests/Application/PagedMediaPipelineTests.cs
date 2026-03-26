using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.Tests.Application;

public class PagedMediaPipelineTests
{
    [Fact]
    public async Task SearchAsync_UsesCache_WhenPresent()
    {
        var cached = new List<MediaSummary>
        {
            new(MediaId.Create("cached-1"), "local", "Cached", MediaType.Paged)
        };

        var searchPort = new CountingSearchPort();
        var cache = new StubCachePort();
        cache.Set("search:demo", cached);

        var pipeline = new PagedMediaPipeline(
            searchPort,
            new StubPageProviderPort(),
            new AllowPolicyEvaluator(),
            cache);

        var results = await pipeline.SearchAsync("demo", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Cached", results[0].Title);
        Assert.Equal(0, searchPort.Calls);
    }

    [Fact]
    public async Task GetChaptersAsync_UsesCache_WhenPresent()
    {
        var cached = new List<MediaChapter>
        {
            new("ch-1", 1, "Chapter One")
        };

        var searchPort = new CountingSearchPort();
        var cache = new StubCachePort();
        cache.Set("chapters:demo-1", cached);

        var pipeline = new PagedMediaPipeline(
            searchPort,
            new StubPageProviderPort(),
            new AllowPolicyEvaluator(),
            cache);

        var results = await pipeline.GetChaptersAsync(MediaId.Create("demo-1"), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Chapter One", results[0].Title);
        Assert.Equal(0, searchPort.Calls);
    }

    [Fact]
    public async Task SearchAsync_Throws_WhenPolicyDenies()
    {
        var pipeline = new PagedMediaPipeline(
            new CountingSearchPort(),
            new StubPageProviderPort(),
            new DenyPolicyEvaluator(),
            new StubCachePort());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.SearchAsync("demo", CancellationToken.None));
    }

    [Fact]
    public async Task GetPageAsync_Throws_WhenPolicyDenies()
    {
        var pipeline = new PagedMediaPipeline(
            new CountingSearchPort(),
            new StubPageProviderPort(),
            new DenyPolicyEvaluator(),
            new StubCachePort());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.GetPageAsync(MediaId.Create("demo"), "ch-1", 0, CancellationToken.None));
    }

    [Fact]
    public async Task GetPagesAsync_ReturnsBatch()
    {
        var pipeline = new PagedMediaPipeline(
            new CountingSearchPort(),
            new StubPageProviderPort(),
            new AllowPolicyEvaluator(),
            new StubCachePort());

        var pages = await pipeline.GetPagesAsync(MediaId.Create("demo"), "ch-1", 5, 3, CancellationToken.None);

        Assert.Equal(3, pages.Pages.Count);
        Assert.True(pages.ReachedEnd);
        Assert.Equal(5, pages.Pages[0].Index);
    }

    private sealed class StubCachePort : ICachePort
    {
        private readonly Dictionary<string, object> _values = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value as T : null);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken) where T : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value;
            return Task.CompletedTask;
        }

        public void Set<T>(string key, T value) where T : class => _values[key] = value;
    }

    private sealed class CountingSearchPort : IMediaSearchPort
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<MediaSummary>>([]);
        }
    }

    private sealed class StubPageProviderPort : IPageProviderPort
    {
        public Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<MediaChapter>>([]);
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

    private sealed class AllowPolicyEvaluator : IPolicyEvaluator
    {
        public CapabilityDecision Evaluate(CapabilityRequest request)
        {
            return new CapabilityDecision(true, null);
        }
    }

    private sealed class DenyPolicyEvaluator : IPolicyEvaluator
    {
        public CapabilityDecision Evaluate(CapabilityRequest request)
        {
            return new CapabilityDecision(false, "Denied");
        }
    }

}
