using EMMA.Api;
using EMMA.Api.Embedded;
using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Contracts.Api.V1;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;

namespace EMMA.Tests.Api;

public sealed class EmbeddedPagedMediaApiTests
{
    [Fact]
    public async Task SearchAsync_ReturnsResultPayload()
    {
        var searchPort = new StubSearchPort();
        var pagePort = new StubPageProviderPort();
        var runtime = EmbeddedRuntimeFactory.Create(
            searchPort,
            pagePort,
            new HostPolicyEvaluator(),
            metadataCache: new InMemoryCachePort());

        var api = new EmbeddedPagedMediaApi(runtime);

        var response = await api.SearchAsync(new SearchRequest
        {
            Query = "demo",
            Context = new ApiRequestContext
            {
                CorrelationId = "test",
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                ClientId = "local"
            }
        }, CancellationToken.None);

        Assert.Equal(SearchResponse.OutcomeOneofCase.Result, response.OutcomeCase);
        Assert.Single(response.Result.Items);
        Assert.Equal(ApiMediaType.Paged, response.Result.Items[0].MediaType);
    }

    [Fact]
    public async Task SearchAsync_MapsAudioMediaType()
    {
        var searchPort = new StubSearchPort(MediaType.Audio);
        var pagePort = new StubPageProviderPort();
        var runtime = EmbeddedRuntimeFactory.Create(
            searchPort,
            pagePort,
            new HostPolicyEvaluator(),
            metadataCache: new InMemoryCachePort());

        var api = new EmbeddedPagedMediaApi(runtime);

        var response = await api.SearchAsync(new SearchRequest
        {
            Query = "audio-demo",
            Context = new ApiRequestContext
            {
                CorrelationId = "test",
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O"),
                ClientId = "local"
            }
        }, CancellationToken.None);

        Assert.Equal(SearchResponse.OutcomeOneofCase.Result, response.OutcomeCase);
        Assert.Single(response.Result.Items);
        Assert.Equal(ApiMediaType.Audio, response.Result.Items[0].MediaType);
    }

    private sealed class StubSearchPort(MediaType mediaType = MediaType.Paged) : IMediaSearchPort
    {
        public Task<IReadOnlyList<MediaSummary>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            IReadOnlyList<MediaSummary> results =
            [
                new(MediaId.Create("demo-1"), "test", "Demo", mediaType)
            ];
            return Task.FromResult(results);
        }
    }

    private sealed class StubPageProviderPort : IPageProviderPort
    {
        public Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(MediaId mediaId, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<MediaChapter>>([
                new MediaChapter("ch-1", 1, "Chapter")
            ]);
        }

        public Task<MediaPage> GetPageAsync(
            MediaId mediaId,
            string chapterId,
            int pageIndex,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new MediaPage("page-1", pageIndex, new Uri("https://example.invalid/page.jpg")));
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
