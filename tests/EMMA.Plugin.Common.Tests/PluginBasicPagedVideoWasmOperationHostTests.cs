using System.Text.Json.Serialization;

namespace EMMA.Plugin.Common.Tests;

public class PluginBasicPagedVideoWasmOperationHostTests
{
    [Fact]
    public void Invoke_VideoStreams_ReturnsSerializedStreams()
    {
        var host = new TestVideoHost();

        var result = host.Invoke(new OperationRequest(
            PluginOperationNames.VideoStreams,
            "media-1",
            PluginMediaTypes.Video,
            null,
            null));

        Assert.False(result.isError);
        Assert.Contains("stream-1", result.payloadJson, StringComparison.Ordinal);
        Assert.Equal("application/json", result.contentType);
    }

    [Fact]
    public void Invoke_Chapters_AcceptsVideoMediaRequests()
    {
        var host = new TestVideoHost();

        var result = host.Invoke(new OperationRequest(
            PluginOperationNames.Chapters,
            "media-1",
            PluginMediaTypes.Video,
            null,
            null));

        Assert.False(result.isError);
        Assert.Contains("chapter-1", result.payloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteOperationForCli_VideoSegment_ReturnsSerializedSegment()
    {
        var host = new TestVideoHost();

        var payload = host.ExecuteOperationForCli(
            PluginOperationNames.VideoSegment,
            ["media-1", "stream-1", "0"],
            string.Empty);

        Assert.Contains("application/octet-stream", payload, StringComparison.Ordinal);
        Assert.Contains("AQID", payload, StringComparison.Ordinal);
    }

    private sealed class TestVideoHost : PluginBasicPagedVideoWasmOperationHost<TestChapterWire>
    {
        private static readonly PluginBasicPagedWasmHostOptions<TestChapterWire> HostOptions = new(
            HandshakeVersion: "1.0.0",
            HandshakeMessage: "test host",
            CapabilityProfile: PluginCapabilityProfile.PagedAndVideo,
            HandshakeTypeInfo: TestJsonContext.Default.HandshakeResponse,
            CapabilityTypeInfo: TestJsonContext.Default.CapabilityItemArray,
            SearchTypeInfo: TestJsonContext.Default.SearchItemArray,
            ChapterTypeInfo: TestJsonContext.Default.ChapterItemArray,
            ChapterInvokeTypeInfo: TestJsonContext.Default.TestChapterWireArray,
            PageTypeInfo: TestJsonContext.Default.PageItem,
            PageArrayTypeInfo: TestJsonContext.Default.PageItemArray,
            OperationResultTypeInfo: TestJsonContext.Default.OperationResult,
            BenchmarkTypeInfo: TestJsonContext.Default.BenchmarkResult,
            NetworkBenchmarkTypeInfo: TestJsonContext.Default.NetworkBenchmarkResult);

        public TestVideoHost()
            : base(HostOptions)
        {
        }

        protected override JsonTypeInfo<VideoStreamOperationItem[]> VideoStreamArrayTypeInfo =>
            TestJsonContext.Default.VideoStreamOperationItemArray;

        protected override JsonTypeInfo<VideoSegmentOperationItem> VideoSegmentTypeInfo =>
            TestJsonContext.Default.VideoSegmentOperationItem;

        protected override string? FetchSearchPayload(PluginSearchQuery parsedQuery) => "search-payload";

        protected override (IReadOnlyList<SearchItem> Results, long ParseMs, long MapMs) SearchFromPayloadWithTimings(string payloadJson) =>
            ([new SearchItem("media-1", "test", "Title", PluginMediaTypes.Paged)], 1, 1);

        protected override string? FetchChaptersPayload(string mediaId) => "chapters-payload";

        protected override IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson) =>
            [new ChapterItem("chapter-1", 1, "Chapter 1")];

        protected override IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson) =>
            [new ChapterOperationItem("chapter-1", 1, "Chapter 1", ["group-1"])];

        protected override TestChapterWire MapChapterOperationItem(ChapterOperationItem item) =>
            new(item.id, item.number, item.title, item.uploaderGroups);

        protected override string? FetchAtHomePayload(string chapterId) => "pages-payload";

        protected override PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson) =>
            new($"{chapterId}-page-{pageIndex}", pageIndex, $"https://example.invalid/{chapterId}/{pageIndex}");

        protected override IReadOnlyList<PageItem> GetPagesFromPayload(string chapterId, int startIndex, int count, string payloadJson) =>
            Enumerable.Range(startIndex, count)
                .Select(index => new PageItem($"{chapterId}-page-{index}", index, $"https://example.invalid/{chapterId}/{index}"))
                .ToArray();

        protected override IReadOnlyList<VideoStreamOperationItem> GetVideoStreams(string mediaId) =>
            [new VideoStreamOperationItem("stream-1", "Main", "https://example.invalid/playlist.m3u8")];

        protected override VideoSegmentOperationItem? GetVideoSegment(string mediaId, string streamId, uint sequence) =>
            new("application/octet-stream", Convert.ToBase64String([1, 2, 3]));
    }

    internal sealed record TestChapterWire(string id, int number, string title, string[] uploaderGroups);

    [JsonSerializable(typeof(HandshakeResponse))]
    [JsonSerializable(typeof(CapabilityItem[]))]
    [JsonSerializable(typeof(SearchItem[]))]
    [JsonSerializable(typeof(ChapterItem[]))]
    [JsonSerializable(typeof(TestChapterWire[]))]
    [JsonSerializable(typeof(PageItem))]
    [JsonSerializable(typeof(PageItem[]))]
    [JsonSerializable(typeof(OperationResult))]
    [JsonSerializable(typeof(BenchmarkResult))]
    [JsonSerializable(typeof(NetworkBenchmarkResult))]
    [JsonSerializable(typeof(VideoStreamOperationItem[]))]
    [JsonSerializable(typeof(VideoSegmentOperationItem))]
    internal sealed partial class TestJsonContext : JsonSerializerContext
    {
    }
}