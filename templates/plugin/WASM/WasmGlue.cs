using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Plugin.Common;
using EMMA.TemplatePlugin.Core;
using LibraryWorld;
using LibraryWorld.wit.imports.emma.plugin;

namespace EMMA.TemplatePlugin.WASM
{
    internal sealed class WasmPluginOperationHost : PluginBasicPagedWasmOperationHost<WasmChapterOperationItem>
    {
        private static readonly PluginBasicPagedWasmHostOptions<WasmChapterOperationItem> HostOptions = new(
            HandshakeVersion: "1.0.0",
            HandshakeMessage: "EMMA wasm component ready",
            CapabilityProfile: PluginCapabilityProfile.PagedOnly,
            HandshakeTypeInfo: WasmJsonContext.Default.HandshakeResponse,
            CapabilityTypeInfo: WasmJsonContext.Default.CapabilityItemArray,
            SearchTypeInfo: WasmJsonContext.Default.SearchItemArray,
            ChapterTypeInfo: WasmJsonContext.Default.ChapterItemArray,
            ChapterInvokeTypeInfo: WasmJsonContext.Default.WasmChapterOperationItemArray,
            PageTypeInfo: WasmJsonContext.Default.PageItem,
            PageArrayTypeInfo: WasmJsonContext.Default.PageItemArray,
            OperationResultTypeInfo: WasmJsonContext.Default.OperationResult,
            BenchmarkTypeInfo: WasmJsonContext.Default.BenchmarkResult,
            NetworkBenchmarkTypeInfo: WasmJsonContext.Default.NetworkBenchmarkResult);

        private readonly WasmClient _client = new();

        public WasmPluginOperationHost()
            : base(HostOptions)
        {
        }

        protected override string? FetchSearchPayload(PluginSearchQuery parsedQuery) => _client.FetchSearchPayload(parsedQuery);

        protected override (IReadOnlyList<SearchItem> Results, long ParseMs, long MapMs) SearchFromPayloadWithTimings(string payloadJson)
        {
            var result = _client.SearchFromPayloadWithTimings(payloadJson);
            return (result.Results, result.ParseMs, result.MapMs);
        }

        protected override string? FetchChaptersPayload(string mediaId) => _client.FetchChaptersPayload(mediaId);

        protected override IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson) =>
            _client.GetChaptersFromPayload(mediaId, payloadJson);

        protected override IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson) =>
            _client.GetChapterOperationItemsFromPayload(mediaId, payloadJson);

        protected override WasmChapterOperationItem MapChapterOperationItem(ChapterOperationItem item) =>
            new(
                item.id,
                item.number,
                item.title,
                [.. item.uploaderGroups ?? []]);

        protected override string? FetchAtHomePayload(string chapterId) => _client.FetchAtHomePayload(chapterId);

        protected override PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson) =>
            _client.GetPageFromPayload(chapterId, pageIndex, payloadJson);

        protected override IReadOnlyList<PageItem> GetPagesFromPayload(string chapterId, int startIndex, int count, string payloadJson) =>
            _client.GetPagesFromPayload(chapterId, startIndex, count, payloadJson);
    }

    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(HandshakeResponse))]
    [JsonSerializable(typeof(CapabilityItem[]))]
    [JsonSerializable(typeof(MetadataItem))]
    [JsonSerializable(typeof(IReadOnlyList<MetadataItem>))]
    [JsonSerializable(typeof(List<MetadataItem>))]
    [JsonSerializable(typeof(SearchItem[]))]
    [JsonSerializable(typeof(ChapterItem[]))]
    [JsonSerializable(typeof(WasmChapterOperationItem[]))]
    [JsonSerializable(typeof(PageItem))]
    [JsonSerializable(typeof(PageItem[]))]
    [JsonSerializable(typeof(OperationResult))]
    [JsonSerializable(typeof(BenchmarkResult))]
    [JsonSerializable(typeof(NetworkBenchmarkResult))]
    internal sealed partial class WasmJsonContext : JsonSerializerContext
    {
    }

    internal sealed record WasmChapterOperationItem(
        string id,
        int number,
        string title,
        string[] uploaderGroups);
}