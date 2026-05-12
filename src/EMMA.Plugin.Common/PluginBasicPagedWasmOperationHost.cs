using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;

namespace EMMA.Plugin.Common;

/// <summary>
/// Describes the standard JSON metadata and default responses used by <see cref="PluginBasicPagedWasmOperationHost{TChapterOperationItem}"/>.
/// </summary>
/// <typeparam name="TChapterOperationItem">The serialized chapter-operation item type used for invoke responses.</typeparam>
/// <param name="HandshakeVersion">The version returned by the standard handshake operation.</param>
/// <param name="HandshakeMessage">The message returned by the standard handshake operation.</param>
/// <param name="CapabilityProfile">The default capability profile advertised by the host.</param>
/// <param name="HandshakeTypeInfo">The JSON type metadata for <see cref="HandshakeResponse"/>.</param>
/// <param name="CapabilityTypeInfo">The JSON type metadata for <see cref="CapabilityItem"/> arrays.</param>
/// <param name="SearchTypeInfo">The JSON type metadata for <see cref="SearchItem"/> arrays.</param>
/// <param name="ChapterTypeInfo">The JSON type metadata for <see cref="ChapterItem"/> arrays.</param>
/// <param name="ChapterInvokeTypeInfo">The JSON type metadata for chapter-operation item arrays.</param>
/// <param name="PageTypeInfo">The JSON type metadata for <see cref="PageItem"/>.</param>
/// <param name="PageArrayTypeInfo">The JSON type metadata for <see cref="PageItem"/> arrays.</param>
/// <param name="OperationResultTypeInfo">The JSON type metadata for <see cref="OperationResult"/>.</param>
/// <param name="BenchmarkTypeInfo">The JSON type metadata for <see cref="BenchmarkResult"/>.</param>
/// <param name="NetworkBenchmarkTypeInfo">The JSON type metadata for <see cref="NetworkBenchmarkResult"/>.</param>
public sealed record PluginBasicPagedWasmHostOptions<TChapterOperationItem>(
    string HandshakeVersion,
    string HandshakeMessage,
    PluginCapabilityProfile CapabilityProfile,
    JsonTypeInfo<HandshakeResponse> HandshakeTypeInfo,
    JsonTypeInfo<CapabilityItem[]> CapabilityTypeInfo,
    JsonTypeInfo<SearchItem[]> SearchTypeInfo,
    JsonTypeInfo<ChapterItem[]> ChapterTypeInfo,
    JsonTypeInfo<TChapterOperationItem[]> ChapterInvokeTypeInfo,
    JsonTypeInfo<PageItem> PageTypeInfo,
    JsonTypeInfo<PageItem[]> PageArrayTypeInfo,
    JsonTypeInfo<OperationResult> OperationResultTypeInfo,
    JsonTypeInfo<BenchmarkResult> BenchmarkTypeInfo,
    JsonTypeInfo<NetworkBenchmarkResult> NetworkBenchmarkTypeInfo);

/// <summary>
/// Provides a reusable WASM host implementation for basic paged-media plugins that fetch payloads from an external provider.
/// </summary>
/// <typeparam name="TChapterOperationItem">The serialized chapter-operation item type used for invoke responses.</typeparam>
public abstract class PluginBasicPagedWasmOperationHost<TChapterOperationItem>
{
    private readonly PluginBasicPagedWasmHostOptions<TChapterOperationItem> _options;
    private readonly PluginOperationDispatcher _invokeDispatcher;
    private readonly IReadOnlyDictionary<string, Func<string[], string, string>> _cliHandlers;

    /// <summary>
    /// Initializes the standard paged-media WASM host with the supplied response metadata and defaults.
    /// </summary>
    /// <param name="options">The host options describing the standard operation responses and JSON type metadata.</param>
    protected PluginBasicPagedWasmOperationHost(PluginBasicPagedWasmHostOptions<TChapterOperationItem> options)
    {
        _options = options;

        var builder = new PluginWasmHostBuilder()
            .AddStandardOperations(
                Handshake,
                _options.HandshakeTypeInfo,
                Capabilities,
                _options.CapabilityTypeInfo)
            .AddStandardPagedCliOperations(
                Search,
                _options.SearchTypeInfo,
                Chapters,
                _options.ChapterTypeInfo,
                SerializePageForCli,
                SerializePagesForCli,
                SerializeInvokeForCli)
            .AddCliHandler(PluginOperationNames.Benchmark, (args, _) => Benchmark(args))
            .AddCliHandler(PluginOperationNames.BenchmarkNetwork, BenchmarkNetwork);

        ConfigureCustomCliHandlers(builder);
        builder.ConfigureInvoke(dispatcher => ConfigureCustomInvokeHandlers(
            dispatcher
                .RegisterPagedOperations(
                    search: request =>
                    {
                        var searchArgs = PluginSearchQuery.Parse(request.argsJson);
                        return PluginWasmInvokeScaffold.BuildJsonResult(
                            Search(searchArgs, request.payloadJson ?? string.Empty),
                            _options.SearchTypeInfo);
                    },
                    chapters: request => PluginWasmInvokeScaffold.BuildJsonResult(
                        BuildChapterOperationItems(request.ResolveMediaId(), request.payloadJson ?? string.Empty),
                        _options.ChapterInvokeTypeInfo),
                    page: request => PluginWasmInvokeScaffold.BuildNullableJsonResult(
                        Page(
                            request.ResolveMediaId(),
                            request.ResolveChapterId(),
                            request.ResolvePageIndex(),
                            request.payloadJson ?? string.Empty),
                        _options.PageTypeInfo),
                    pages: request => PluginWasmInvokeScaffold.BuildJsonResult(
                        Pages(
                            request.ResolveMediaId(),
                            request.ResolveChapterId(),
                            request.ResolveStartIndex(),
                            request.ResolveCount(),
                            request.payloadJson ?? string.Empty),
                        _options.PageArrayTypeInfo),
                    supportsChapterRequests: SupportsChapterRequests)
                .Register(PluginOperationNames.Benchmark, request =>
                {
                    var iterations = Math.Max(1, PluginJsonArgs.GetInt32(request.argsJson, "iterations") ?? 5000);
                    return PluginWasmInvokeScaffold.BuildJsonResult(Benchmark([iterations.ToString()]));
                })
                .Register(PluginOperationNames.BenchmarkNetwork, request =>
                {
                    var query = PluginJsonArgs.GetString(request.argsJson, "query");
                    return PluginWasmInvokeScaffold.BuildJsonResult(BenchmarkNetwork([query], request.payloadJson ?? string.Empty));
                })));

        var host = builder.Build();
        _invokeDispatcher = host.InvokeDispatcher;
        _cliHandlers = host.CliHandlers;
    }

    /// <summary>
    /// Executes a CLI operation using the registered standard and custom WASM handlers.
    /// </summary>
    /// <param name="operation">The operation name to execute.</param>
    /// <param name="args">The positional CLI arguments for the operation.</param>
    /// <param name="inputPayload">The input payload supplied on standard input.</param>
    /// <returns>The serialized CLI response.</returns>
    public string ExecuteOperationForCli(string operation, string[] args, string inputPayload)
    {
        return PluginWasmCliOperationDispatcher.Execute(operation, args, inputPayload, _cliHandlers);
    }

    /// <summary>
    /// Returns the standard handshake response for the host.
    /// </summary>
    /// <returns>The handshake response.</returns>
    public HandshakeResponse Handshake()
    {
        return new HandshakeResponse(_options.HandshakeVersion, _options.HandshakeMessage);
    }

    /// <summary>
    /// Returns the default capability declarations for the configured capability profile.
    /// </summary>
    /// <returns>The advertised plugin capabilities.</returns>
    public CapabilityItem[] Capabilities()
    {
        return PluginCapabilityProfiles.Create(_options.CapabilityProfile);
    }

    /// <summary>
    /// Executes a standard search operation using the raw query string and optional payload.
    /// </summary>
    /// <param name="query">The raw search query string.</param>
    /// <param name="payloadJson">The optional pre-fetched payload to search from.</param>
    /// <returns>The mapped search results.</returns>
    public SearchItem[] Search(string query, string payloadJson)
    {
        var parsedQuery = PluginSearchQuery.Parse(query, fallbackQuery: query);
        return Search(parsedQuery, payloadJson);
    }

    /// <summary>
    /// Executes a standard chapter-listing operation for the specified media item.
    /// </summary>
    /// <param name="mediaId">The media identifier whose chapters should be returned.</param>
    /// <param name="payloadJson">The optional pre-fetched chapter payload.</param>
    /// <returns>The mapped chapter results.</returns>
    public ChapterItem[] Chapters(string mediaId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            payloadJson,
            () => FetchChaptersPayload(mediaId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        return [.. GetChaptersFromPayload(mediaId, payloadJson)];
    }

    /// <summary>
    /// Resolves a single page from the provider payload for the specified chapter.
    /// </summary>
    /// <param name="mediaId">The parent media identifier.</param>
    /// <param name="chapterId">The chapter identifier whose page should be returned.</param>
    /// <param name="pageIndex">The zero-based page index to resolve.</param>
    /// <param name="payloadJson">The optional pre-fetched page payload.</param>
    /// <returns>The resolved page, or <see langword="null"/> when the page cannot be produced.</returns>
    public PageItem? Page(string mediaId, string chapterId, uint pageIndex, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
        {
            return null;
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            payloadJson,
            () => FetchAtHomePayload(chapterId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        return GetPageFromPayload(chapterId, checked((int)pageIndex), payloadJson);
    }

    /// <summary>
    /// Resolves a contiguous page range from the provider payload for the specified chapter.
    /// </summary>
    /// <param name="mediaId">The parent media identifier.</param>
    /// <param name="chapterId">The chapter identifier whose pages should be returned.</param>
    /// <param name="startIndex">The zero-based starting page index.</param>
    /// <param name="count">The maximum number of pages to return.</param>
    /// <param name="payloadJson">The optional pre-fetched page payload.</param>
    /// <returns>The resolved page range.</returns>
    public PageItem[] Pages(string mediaId, string chapterId, uint startIndex, uint count, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId) || count == 0)
        {
            return [];
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            payloadJson,
            () => FetchAtHomePayload(chapterId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        return [.. GetPagesFromPayload(chapterId, checked((int)startIndex), checked((int)count), payloadJson)];
    }

    /// <summary>
    /// Dispatches an invoke request through the standard and custom invoke handlers.
    /// </summary>
    /// <param name="request">The invoke request to dispatch.</param>
    /// <returns>The invoke result produced by the registered handlers.</returns>
    public OperationResult Invoke(OperationRequest request)
    {
        var operation = request.NormalizedOperation();
        if (operation == PluginOperationNames.Search && PluginEnvironment.IsDevelopmentMode())
        {
            var searchArgs = PluginSearchQuery.Parse(request.argsJson);
            PluginWasmDiagnosticsScaffold.DevLog($"[DEBUG] Invoke search: argsJson={request.argsJson}");
            PluginWasmDiagnosticsScaffold.DevLog($"[DEBUG] Parsed searchArgs.Query={searchArgs.Query}");
        }

        return _invokeDispatcher.Dispatch(request);
    }

    /// <summary>
    /// Determines whether the current request supports chapter and page operations.
    /// </summary>
    /// <param name="request">The current operation request.</param>
    /// <returns><see langword="true"/> when the request should be treated as chapter/page capable.</returns>
    protected virtual bool SupportsChapterRequests(OperationRequest request)
    {
        return request.IsPagedMediaRequest();
    }

    /// <summary>
    /// Allows derived hosts to register additional CLI handlers before the host is built.
    /// </summary>
    /// <param name="builder">The WASM host builder being configured.</param>
    protected virtual void ConfigureCustomCliHandlers(PluginWasmHostBuilder builder)
    {
    }

    /// <summary>
    /// Allows derived hosts to register or replace invoke handlers beyond the standard paged-media operations.
    /// </summary>
    /// <param name="dispatcher">The dispatcher preconfigured with the standard paged-media handlers.</param>
    /// <returns>The dispatcher instance that should be used by the host.</returns>
    protected virtual PluginOperationDispatcher ConfigureCustomInvokeHandlers(PluginOperationDispatcher dispatcher)
    {
        return dispatcher;
    }

    /// <summary>
    /// Executes the standard CPU benchmark CLI and invoke operation.
    /// </summary>
    /// <param name="args">The benchmark CLI arguments.</param>
    /// <returns>The serialized benchmark result.</returns>
    protected virtual string Benchmark(string[] args)
    {
        return PluginWasmDiagnosticsScaffold.RunCpuBenchmark(args, _options.BenchmarkTypeInfo);
    }

    /// <summary>
    /// Executes the standard network benchmark CLI and invoke operation.
    /// </summary>
    /// <param name="args">The benchmark CLI arguments.</param>
    /// <param name="stdinPayload">The optional payload supplied on standard input.</param>
    /// <returns>The serialized network benchmark result.</returns>
    protected virtual string BenchmarkNetwork(string[] args, string stdinPayload)
    {
        return PluginWasmDiagnosticsScaffold.RunNetworkBenchmark(
            args,
            stdinPayload,
            parsedQuery => PluginPayloadResolvers.ResolveProvidedOrFetched(
                PluginPayload.NormalizePayload(stdinPayload),
                () => FetchSearchPayload(parsedQuery)),
            _options.NetworkBenchmarkTypeInfo);
    }

            /// <summary>
            /// Fetches or resolves the search payload for a parsed query when no payload was provided by the caller.
            /// </summary>
            /// <param name="parsedQuery">The parsed search query.</param>
            /// <returns>The resolved payload text, or <see langword="null"/> when no payload could be produced.</returns>
    protected abstract string? FetchSearchPayload(PluginSearchQuery parsedQuery);

            /// <summary>
            /// Parses provider search results from the supplied payload and returns timing information for diagnostics.
            /// </summary>
            /// <param name="payloadJson">The provider payload to parse.</param>
            /// <returns>The parsed results with parse and map timing metrics.</returns>
    protected abstract (IReadOnlyList<SearchItem> Results, long ParseMs, long MapMs) SearchFromPayloadWithTimings(string payloadJson);

            /// <summary>
            /// Fetches the chapter payload for a media item when no payload was provided by the caller.
            /// </summary>
            /// <param name="mediaId">The media identifier whose chapters should be fetched.</param>
            /// <returns>The resolved payload text, or <see langword="null"/> when no payload could be produced.</returns>
    protected abstract string? FetchChaptersPayload(string mediaId);

            /// <summary>
            /// Maps a provider chapter payload into standard chapter items.
            /// </summary>
            /// <param name="mediaId">The media identifier associated with the payload.</param>
            /// <param name="payloadJson">The provider payload to map.</param>
            /// <returns>The mapped chapter items.</returns>
    protected abstract IReadOnlyList<ChapterItem> GetChaptersFromPayload(string mediaId, string payloadJson);

            /// <summary>
            /// Maps a provider chapter payload into chapter-operation items used by invoke responses.
            /// </summary>
            /// <param name="mediaId">The media identifier associated with the payload.</param>
            /// <param name="payloadJson">The provider payload to map.</param>
            /// <returns>The mapped chapter-operation items.</returns>
    protected abstract IReadOnlyList<ChapterOperationItem> GetChapterOperationItemsFromPayload(string mediaId, string payloadJson);

            /// <summary>
            /// Converts a standard chapter-operation item into the transport-specific serialized invoke shape.
            /// </summary>
            /// <param name="item">The standard chapter-operation item.</param>
            /// <returns>The serialized chapter-operation item.</returns>
    protected abstract TChapterOperationItem MapChapterOperationItem(ChapterOperationItem item);

            /// <summary>
            /// Fetches the at-home page payload for a chapter when no payload was provided by the caller.
            /// </summary>
            /// <param name="chapterId">The chapter identifier whose at-home payload should be fetched.</param>
            /// <returns>The resolved payload text, or <see langword="null"/> when no payload could be produced.</returns>
    protected abstract string? FetchAtHomePayload(string chapterId);

            /// <summary>
            /// Maps a provider page payload into a single page item.
            /// </summary>
            /// <param name="chapterId">The chapter identifier associated with the payload.</param>
            /// <param name="pageIndex">The zero-based page index to resolve.</param>
            /// <param name="payloadJson">The provider payload to map.</param>
            /// <returns>The mapped page item, or <see langword="null"/> when no page could be produced.</returns>
    protected abstract PageItem? GetPageFromPayload(string chapterId, int pageIndex, string payloadJson);

            /// <summary>
            /// Maps a provider page payload into a range of page items.
            /// </summary>
            /// <param name="chapterId">The chapter identifier associated with the payload.</param>
            /// <param name="startIndex">The zero-based starting page index.</param>
            /// <param name="count">The maximum number of pages to return.</param>
            /// <param name="payloadJson">The provider payload to map.</param>
            /// <returns>The mapped page items.</returns>
    protected abstract IReadOnlyList<PageItem> GetPagesFromPayload(string chapterId, int startIndex, int count, string payloadJson);

            /// <summary>
            /// Executes a parsed search request by resolving payloads, emitting diagnostics, and mapping provider results.
            /// </summary>
            /// <param name="parsedQuery">The parsed search query.</param>
            /// <param name="payloadJson">The optional pre-fetched search payload.</param>
            /// <returns>The mapped search results.</returns>
    protected virtual SearchItem[] Search(PluginSearchQuery parsedQuery, string payloadJson)
    {
        PluginWasmDiagnosticsScaffold.DevLog($"[SEARCH] Called with query='{parsedQuery.Query}' (empty={string.IsNullOrWhiteSpace(parsedQuery.Query)})");

        if (string.IsNullOrWhiteSpace(parsedQuery.Query)
            && parsedQuery.Filters.Count == 0
            && parsedQuery.QueryAdditions.Count == 0)
        {
            PluginWasmDiagnosticsScaffold.DevLog("[SEARCH] Returning empty results because query and filters are empty");
            return [];
        }

        var totalStopwatch = Stopwatch.StartNew();
        var fetchMs = 0L;
        var payloadWasFetched = false;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            payloadWasFetched = true;
            var fetchStopwatch = Stopwatch.StartNew();
            payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
                payloadJson,
                () => FetchSearchPayload(parsedQuery));
            fetchStopwatch.Stop();
            fetchMs = fetchStopwatch.ElapsedMilliseconds;
            PluginWasmDiagnosticsScaffold.DevLog($"[SEARCH] Fetched payload in {fetchMs}ms, length={payloadJson.Length}");
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            PluginWasmDiagnosticsScaffold.DevLog("[SEARCH] Payload is empty after fetch, returning []");

            totalStopwatch.Stop();
            PluginWasmDiagnosticsScaffold.EmitSearchSplitTiming(parsedQuery.Query, payloadJson, fetchMs, 0, 0, 0, payloadWasFetched, totalStopwatch.ElapsedMilliseconds);
            return [];
        }

        PluginWasmDiagnosticsScaffold.DevLog($"[SEARCH] Parsing payload for query='{parsedQuery.Query}'");

        var parseMapResult = SearchFromPayloadWithTimings(payloadJson);
        PluginWasmDiagnosticsScaffold.DevLog($"[SEARCH] Parse completed, got {parseMapResult.Results.Count} results");

        totalStopwatch.Stop();

        PluginWasmDiagnosticsScaffold.EmitSearchSplitTiming(
            parsedQuery.Query,
            payloadJson,
            fetchMs,
            parseMapResult.ParseMs,
            parseMapResult.MapMs,
            parseMapResult.Results.Count,
            payloadWasFetched,
            totalStopwatch.ElapsedMilliseconds);

        return [.. parseMapResult.Results];
    }

    private TChapterOperationItem[] BuildChapterOperationItems(string mediaId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return [];
        }

        payloadJson = PluginPayloadResolvers.ResolveProvidedOrFetched(
            payloadJson,
            () => FetchChaptersPayload(mediaId));
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        var operationItems = GetChapterOperationItemsFromPayload(mediaId, payloadJson);
        if (operationItems.Count == 0)
        {
            return [];
        }

        return PluginWasmPagingJsonHelpers.MapChapterOperationItems(operationItems, MapChapterOperationItem).ToArray();
    }

    private string SerializePageForCli(string[] args, string stdinPayload)
    {
        return PluginWasmPagingJsonHelpers.SerializePageForCli(
            args,
            stdinPayload,
            Page,
            _options.PageTypeInfo);
    }

    private string SerializePagesForCli(string[] args, string stdinPayload)
    {
        return PluginWasmPagingJsonHelpers.SerializePagesForCli(
            args,
            stdinPayload,
            Pages,
            _options.PageArrayTypeInfo);
    }

    private string SerializeInvokeForCli(string[] args, string stdinPayload)
    {
        return PluginWasmInvokeScaffold.SerializeInvokeForCli(
            args,
            stdinPayload,
            Invoke,
            _options.OperationResultTypeInfo);
    }
}