using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using EMMA.Domain;
using EMMA.Plugin.Common;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Provides high-level operations for invoking WASM-based plugins.
/// </summary>
public interface IWasmPluginRuntimeHost
{
    /// <summary>
    /// Determines whether the supplied manifest resolves to a WASM plugin.
    /// </summary>
    bool IsWasmPlugin(PluginManifest manifest);

    /// <summary>
    /// Performs an optional warm-up invocation for a WASM plugin.
    /// </summary>
    Task WarmupAsync(PluginManifest manifest, CancellationToken cancellationToken);

    /// <summary>
    /// Performs handshake for a WASM plugin.
    /// </summary>
    Task<PluginHandshakeStatus> HandshakeAsync(PluginManifest manifest, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the benchmark operation for a WASM plugin.
    /// </summary>
    Task<string> BenchmarkAsync(PluginRecord record, int iterations, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the network benchmark operation for a WASM plugin.
    /// </summary>
    Task<string> BenchmarkNetworkAsync(PluginRecord record, string query, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a search operation and returns the raw JSON payload.
    /// </summary>
    Task<string> SearchJsonAsync(PluginRecord record, string query, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a search operation and returns mapped media summaries.
    /// </summary>
    Task<IReadOnlyList<MediaSummary>> SearchAsync(PluginRecord record, string query, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves suggestions for a lookup-backed search control.
    /// </summary>
    Task<IReadOnlyList<SearchSuggestionItem>> GetSearchSuggestionsAsync(
        PluginRecord record,
        SearchSuggestionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the available video streams for a media item.
    /// </summary>
    Task<IReadOnlyList<WasmVideoStreamItem>> GetVideoStreamsAsync(PluginRecord record, MediaId mediaId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a single video segment for a stream.
    /// </summary>
    Task<WasmVideoSegmentResult?> GetVideoSegmentAsync(PluginRecord record, MediaId mediaId, string streamId, int sequence, CancellationToken cancellationToken);

    /// <summary>
    /// Gets chapters for a media item.
    /// </summary>
    Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(PluginRecord record, MediaId mediaId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a single page for a chapter.
    /// </summary>
    Task<MediaPage> GetPageAsync(PluginRecord record, MediaId mediaId, string chapterId, int pageIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a batch of pages for a chapter.
    /// </summary>
    Task<MediaPagesResult> GetPagesAsync(PluginRecord record, MediaId mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken);

    /// <summary>
    /// Enriches existing search results with additional metadata.
    /// </summary>
    Task<IReadOnlyList<MediaSummary>> EnrichSearchMetadataAsync(PluginRecord record, IEnumerable<string> mediaIds, IReadOnlyList<MediaSummary>? baseItems = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Invokes a low-level operation on a resolved WASM component.
/// </summary>
public interface IWasmComponentInvoker
{
    /// <summary>
    /// Invokes a WASM component operation.
    /// </summary>
    Task<string> InvokeAsync(
        string componentPath,
        string operation,
        IReadOnlyList<string> operationArgs,
        IReadOnlyList<string>? permittedDomains,
        CancellationToken cancellationToken);
}

/// <summary>
/// Represents the invocation envelope passed to the native WASM component bridge.
/// </summary>
/// <param name="Args">The operation arguments.</param>
/// <param name="PermittedDomains">The permitted network domains.</param>
public sealed record WasmComponentInvokeEnvelope(
    [property: JsonPropertyName("args")] IReadOnlyList<string> Args,
    [property: JsonPropertyName("permittedDomains")] IReadOnlyList<string>? PermittedDomains);

/// <summary>
/// Implements WASM plugin operations on top of the entrypoint resolver and component invoker.
/// </summary>
/// <param name="entrypointResolver">The plugin entrypoint resolver.</param>
/// <param name="invoker">The low-level WASM component invoker.</param>
/// <param name="options">The plugin host options.</param>
/// <param name="metrics">The plugin host metrics collector.</param>
/// <param name="logger">The logger used for WASM runtime diagnostics.</param>
public sealed class WasmPluginRuntimeHost(
    IPluginEntrypointResolver entrypointResolver,
    IWasmComponentInvoker invoker,
    IOptions<PluginHostOptions> options,
    PluginHostMetrics metrics,
    ILogger<WasmPluginRuntimeHost> logger) : IWasmPluginRuntimeHost
{
    private const string HandshakeOperation = "handshake";
    private const string CapabilitiesOperation = "capabilities";
    private const string SearchOperation = "search";
    private const string SearchSuggestionsOperation = "search-suggestions";
    private const string ChaptersOperation = "chapters";
    private const string PageOperation = "page";
    private const string PagesOperation = "pages";
    private const string InvokeOperation = "invoke";
    private const string EnrichSearchMetadataOperation = "enrich-search-metadata";
    private const string BenchmarkNetworkOperation = "benchmark-network";
    private const string PagedMediaType = "paged";
    private const string VideoMediaType = "video";
    private const string VideoStreamsOperation = "video-streams";
    private const string VideoSegmentOperation = "video-segment";

    private readonly IPluginEntrypointResolver _entrypointResolver = entrypointResolver;
    private readonly IWasmComponentInvoker _invoker = invoker;
    private readonly PluginHostMetrics _metrics = metrics;
    private readonly ILogger<WasmPluginRuntimeHost> _logger = logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _batchPagesBackoffUntilByPlugin = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SearchCacheEntry> _searchCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _warmupByPlugin = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _componentPathByPlugin = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WarmupTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BatchPagesRetryBackoff = TimeSpan.FromSeconds(30);

    private readonly record struct SearchCacheEntry(IReadOnlyList<MediaSummary> Results, DateTimeOffset CachedAtUtc);

    /// <summary>
    /// Determines whether the supplied manifest resolves to a WASM plugin.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <returns><see langword="true"/> when the manifest resolves to a WASM component; otherwise, <see langword="false"/>.</returns>
    public bool IsWasmPlugin(PluginManifest manifest)
    {
        _ = options;
        return _entrypointResolver.TryResolveWasmComponent(manifest, out _);
    }

    /// <summary>
    /// Performs an optional warm-up invocation for a WASM plugin.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task WarmupAsync(PluginManifest manifest, CancellationToken cancellationToken)
    {
        if (!IsWasmPlugin(manifest))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_warmupByPlugin.TryGetValue(manifest.Id, out var warmedAt)
            && now - warmedAt <= WarmupTtl)
        {
            return;
        }

        var componentPath = ResolveComponentPath(manifest);
        _componentPathByPlugin[manifest.Id] = componentPath;

        var warmupWatch = Stopwatch.StartNew();
        try
        {
            await RunComponentAsync(componentPath, CapabilitiesOperation, [], null, cancellationToken);
            _warmupByPlugin[manifest.Id] = DateTimeOffset.UtcNow;
            warmupWatch.Stop();

            _logger.LogInformation(
                "WASM warm-up completed for {PluginId} in {ElapsedMs}ms.",
                manifest.Id,
                warmupWatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            warmupWatch.Stop();
            _logger.LogDebug(
                ex,
                "WASM warm-up failed for {PluginId} after {ElapsedMs}ms.",
                manifest.Id,
                warmupWatch.ElapsedMilliseconds);
        }
    }

        /// <summary>
        /// Performs handshake for a WASM plugin.
        /// </summary>
        /// <param name="manifest">The plugin manifest.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The handshake status.</returns>
    public async Task<PluginHandshakeStatus> HandshakeAsync(PluginManifest manifest, CancellationToken cancellationToken)
    {
        if (!_entrypointResolver.TryResolveWasmComponent(manifest, out var componentPath))
        {
            return new PluginHandshakeStatus(
                false,
                "WASM component not found.",
                null,
                DateTimeOffset.UtcNow,
                [],
                0,
                0,
                [],
                []);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var handshakeJson = await RunComponentAsync(componentPath, HandshakeOperation, [], manifest.Permissions?.Domains, cancellationToken);
        var health = DeserializeJson<WasmHealth>(handshakeJson);
        var capabilitiesJson = await RunComponentAsync(componentPath, CapabilitiesOperation, [], manifest.Permissions?.Domains, cancellationToken);
        var capabilities = ParseCapabilities(capabilitiesJson);

        if (health is null)
        {
            return new PluginHandshakeStatus(
                false,
                "WASM component handshake response is invalid.",
                null,
                DateTimeOffset.UtcNow,
                [],
                0,
                0,
                [],
                []);
        }

        var message = string.IsNullOrWhiteSpace(health.Message)
            ? "WASM component runtime ready"
            : health.Message;

        return new PluginHandshakeStatus(
            true,
            message,
            health.Version,
            DateTimeOffset.UtcNow,
            capabilities,
            manifest.Capabilities?.CpuBudgetMs ?? 0,
            manifest.Capabilities?.MemoryMb ?? 0,
            manifest.Permissions?.Domains?.ToArray() ?? [],
            manifest.Permissions?.Paths?.ToArray() ?? []);
    }

    private IReadOnlyList<string> ParseCapabilities(string? capabilitiesJson)
    {
        var typedCapabilities = DeserializeJson<IReadOnlyList<WasmCapabilityItem>>(capabilitiesJson);
        if (typedCapabilities is null || typedCapabilities.Count == 0)
        {
            return [];
        }

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in typedCapabilities)
        {
            if (!string.IsNullOrWhiteSpace(capability.Name))
            {
                normalized.Add(capability.Name.Trim());
            }

            foreach (var operation in capability.Operations)
            {
                if (!string.IsNullOrWhiteSpace(operation))
                {
                    normalized.Add(operation.Trim());
                }
            }
        }

        return [.. normalized];
    }

    /// <summary>
    /// Executes a search operation and returns mapped media summaries.
    /// </summary>
    public async Task<IReadOnlyList<MediaSummary>> SearchAsync(
        PluginRecord record,
        string query,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        var searchCacheKey = $"{record.Manifest.Id}:{normalizedQuery}";
        var cacheNow = DateTimeOffset.UtcNow;
        if (_searchCache.TryGetValue(searchCacheKey, out var cached)
            && cacheNow - cached.CachedAtUtc <= SearchCacheTtl)
        {
            return cached.Results;
        }

        var invokeStopwatch = Stopwatch.StartNew();
        var searchJson = await SearchJsonAsync(record, normalizedQuery, cancellationToken);
        invokeStopwatch.Stop();

        var deserializeStopwatch = Stopwatch.StartNew();
        var searchItems = DeserializeJson<IReadOnlyList<WasmSearchItem>>(searchJson);
        deserializeStopwatch.Stop();

        var totalMs = invokeStopwatch.ElapsedMilliseconds + deserializeStopwatch.ElapsedMilliseconds;
        if (totalMs >= 500)
        {
            _logger.LogInformation(
                "WASM search timings for {PluginId}: invoke={InvokeMs}ms deserialize={DeserializeMs}ms total={TotalMs}ms (queryLength={QueryLength})",
                record.Manifest.Id,
                invokeStopwatch.ElapsedMilliseconds,
                deserializeStopwatch.ElapsedMilliseconds,
                totalMs,
                normalizedQuery.Length);
        }
        else
        {
            _logger.LogDebug(
                "WASM search timings for {PluginId}: invoke={InvokeMs}ms deserialize={DeserializeMs}ms total={TotalMs}ms (queryLength={QueryLength})",
                record.Manifest.Id,
                invokeStopwatch.ElapsedMilliseconds,
                deserializeStopwatch.ElapsedMilliseconds,
                totalMs,
                normalizedQuery.Length);
        }

        if (searchItems is null)
        {
            var truncated = searchJson?.Length > 500 ? string.Concat(searchJson.AsSpan(0, 500), "...") : searchJson;
            throw new InvalidOperationException($"Failed to deserialize WASM search response. Raw response: {truncated}");
        }

        if (searchItems.Count == 0)
        {
            _logger.LogInformation(
                "WASM search returned no results for {PluginId} (queryLength={QueryLength}).",
                record.Manifest.Id,
                normalizedQuery.Length);
            return [];
        }

        var mappedResults = searchItems.Select(item =>
        {
            var metadataDict = item.Metadata is { Count: > 0 }
                ? item.Metadata.ToDictionary(metadata => metadata.key, metadata => metadata.value, StringComparer.OrdinalIgnoreCase)
                : null;

            if (metadataDict is null)
            {
                _logger.LogWarning(
                    "WASM search item has no metadata: id={Id}, source={Source}",
                    item.Id,
                    item.Source);
            }

            return new MediaSummary(
                MediaId.Create(item.Id),
                item.Source ?? record.Manifest.Id,
                item.Title,
                ParseMediaType(item.MediaType),
                string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
                string.IsNullOrWhiteSpace(item.Description) ? null : item.Description,
                metadataDict);
        })
            .ToArray();

        _searchCache[searchCacheKey] = new SearchCacheEntry(mappedResults, DateTimeOffset.UtcNow);

        return mappedResults;
    }

    /// <summary>
    /// Resolves suggestions for a lookup-backed search control.
    /// </summary>
    public async Task<IReadOnlyList<SearchSuggestionItem>> GetSearchSuggestionsAsync(
        PluginRecord record,
        SearchSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var componentPath = ResolveComponentPath(record.Manifest);
        var searchArgs = request.SearchQuery is null
            ? null
            : ToWasmQueryArgs(request.SearchQuery);
        var argsJson = SerializeJson(new WasmSearchSuggestionsArgs(
            request.ControlId,
            request.Query,
            searchArgs,
            request.Limit));

        var suggestionsJson = await RunInvokeOperationAsync(
            componentPath,
            operation: SearchSuggestionsOperation,
            mediaId: null,
            mediaType: null,
            argsJson: argsJson,
            permittedDomains: record.Manifest.Permissions?.Domains,
            cancellationToken: cancellationToken);

        var suggestions = DeserializeJson<IReadOnlyList<WasmSearchSuggestionItem>>(suggestionsJson);
        if (suggestions is null || suggestions.Count == 0)
        {
            return [];
        }

        return [.. suggestions.Select(static item => new SearchSuggestionItem(
            item.Value,
            item.Label,
            item.Description))];
    }

    private static MediaType ParseMediaType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, "video", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Video;
        }

        if (string.Equals(normalized, "audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "music", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "podcast", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Audio;
        }

        return MediaType.Paged;
    }

    /// <summary>
    /// Executes a search operation and returns the raw JSON payload.
    /// </summary>
    public async Task<string> SearchJsonAsync(
        PluginRecord record,
        string query,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var normalizedQuery = query?.Trim() ?? string.Empty;
        var searchArgs = BuildSearchArgs(record.Manifest, normalizedQuery);
        var invokeStopwatch = Stopwatch.StartNew();
        var argsJson = SerializeJson(searchArgs);
        _logger.LogDebug("WASM search invoke: query={Query}, argsJson={ArgsJson}", normalizedQuery, argsJson);
        var operationResult = await RunInvokeOperationWithResultAsync(
            componentPath,
            operation: SearchOperation,
            mediaId: null,
            mediaType: null,
            argsJson: argsJson,
            permittedDomains: record.Manifest.Permissions?.Domains,
            cancellationToken: cancellationToken);
        invokeStopwatch.Stop();

        var searchJson = operationResult.PayloadJson ?? string.Empty;

        _logger.LogDebug(
            "WASM search invoke contentType for {PluginId}: {ContentType}",
            record.Manifest.Id,
            operationResult.ContentType ?? "<null>");

        if (TryParseSearchTimingMetadata(operationResult.ContentType, out var splitTiming))
        {
            _logger.LogInformation(
                "WASM search split timing for {PluginId}: fetch={FetchMs}ms parse={ParseMs}ms map={MapMs}ms pluginTotal={PluginTotalMs}ms resultCount={ResultCount} payloadSource={PayloadSource}",
                record.Manifest.Id,
                splitTiming.FetchMs,
                splitTiming.ParseMs,
                splitTiming.MapMs,
                splitTiming.TotalMs,
                splitTiming.ResultCount,
                splitTiming.PayloadSource);
        }

        if (invokeStopwatch.ElapsedMilliseconds >= 500)
        {
            _logger.LogInformation(
                "WASM search JSON timing for {PluginId}: invoke={InvokeMs}ms (queryLength={QueryLength}, responseBytes={ResponseBytes})",
                record.Manifest.Id,
                invokeStopwatch.ElapsedMilliseconds,
                normalizedQuery.Length,
                searchJson.Length);
        }
        else
        {
            _logger.LogDebug(
                "WASM search JSON timing for {PluginId}: invoke={InvokeMs}ms (queryLength={QueryLength}, responseBytes={ResponseBytes})",
                record.Manifest.Id,
                invokeStopwatch.ElapsedMilliseconds,
                normalizedQuery.Length,
                searchJson.Length);
        }

        return searchJson;
    }

    /// <summary>
    /// Runs the benchmark operation for a WASM plugin.
    /// </summary>
    public async Task<string> BenchmarkAsync(
        PluginRecord record,
        int iterations,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var normalizedIterations = Math.Clamp(iterations, 1, 1000);

        return await RunInvokeOperationAsync(
            componentPath,
            operation: "benchmark",
            mediaId: null,
            mediaType: null,
            argsJson: SerializeJson(new WasmBenchmarkArgs(normalizedIterations)),
            permittedDomains: record.Manifest.Permissions?.Domains,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Runs the network benchmark operation for a WASM plugin.
    /// </summary>
    public async Task<string> BenchmarkNetworkAsync(
        PluginRecord record,
        string query,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var normalizedQuery = string.IsNullOrWhiteSpace(query)
            ? "one piece"
            : query.Trim();

        return await RunInvokeOperationAsync(
            componentPath,
            operation: BenchmarkNetworkOperation,
            mediaId: null,
            mediaType: null,
            argsJson: SerializeJson(new WasmQueryArgs(normalizedQuery)),
            permittedDomains: record.Manifest.Permissions?.Domains,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Gets chapters for a media item.
    /// </summary>
    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(
        PluginRecord record,
        MediaId mediaId,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var chaptersJson = await RunComponentAsync(
            componentPath,
            ChaptersOperation,
            [mediaId.Value],
            record.Manifest.Permissions?.Domains,
            cancellationToken);

        var chapters = DeserializeJson<IReadOnlyList<WasmChapterItem>>(chaptersJson);
        if (chapters is null || chapters.Count == 0)
        {
            return [];
        }

        return [.. chapters.Select(chapter => new MediaChapter(
            chapter.Id,
            chapter.Number,
            chapter.Title,
            chapter.UploaderGroups
                ?.Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? []))];
    }

    /// <summary>
    /// Enriches existing search results with additional metadata.
    /// </summary>
    public async Task<IReadOnlyList<MediaSummary>> EnrichSearchMetadataAsync(
        PluginRecord record,
        IEnumerable<string> mediaIds,
        IReadOnlyList<MediaSummary>? baseItems = null,
        CancellationToken cancellationToken = default)
    {
        var ids = mediaIds?.ToList() ?? [];
        if (ids.Count == 0)
        {
            return [];
        }

        var componentPath = ResolveComponentPath(record.Manifest);
        var baseWasmItems = baseItems is { Count: > 0 }
            ? baseItems.Select(ToWasmSearchItem).ToList()
            : null;
        var argsJson = SerializeJson(new WasmEnrichMediaArgs(ids, baseWasmItems));

        var enrichStopwatch = Stopwatch.StartNew();
        var operationResult = await RunInvokeOperationWithResultAsync(
            componentPath,
            operation: EnrichSearchMetadataOperation,
            mediaId: null,
            mediaType: null,
            argsJson: argsJson,
            permittedDomains: record.Manifest.Permissions?.Domains,
            cancellationToken: cancellationToken);
        enrichStopwatch.Stop();

        var enrichedItems = DeserializeJson<IReadOnlyList<WasmSearchItem>>(
            operationResult.PayloadJson ?? string.Empty);

        if (enrichedItems is null || enrichedItems.Count == 0)
        {
            return baseItems ?? [];
        }

        _logger.LogDebug(
            "WASM enrichment completed for {PluginId}: {ItemCount} items enriched in {ElapsedMs}ms",
            record.Manifest.Id,
            enrichedItems.Count,
            enrichStopwatch.ElapsedMilliseconds);

        var mappedResults = enrichedItems.Select(item =>
        {
            var metadataDict = item.Metadata is { Count: > 0 }
                ? item.Metadata.ToDictionary(metadata => metadata.key, metadata => metadata.value, StringComparer.OrdinalIgnoreCase)
                : null;

            return new MediaSummary(
                MediaId.Create(item.Id),
                item.Source ?? record.Manifest.Id,
                item.Title,
                ParseMediaType(item.MediaType),
                string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
                string.IsNullOrWhiteSpace(item.Description) ? null : item.Description,
                metadataDict);
        })
            .ToArray();

        return mappedResults;
    }

    private static string ParseMediaTypeForWasm(MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.Video => "video",
            MediaType.Audio => "audio",
            MediaType.Paged => "paged",
            _ => "paged"
        };
    }

    private static WasmSearchItem ToWasmSearchItem(MediaSummary item)
    {
        var metadata = item.Metadata is { Count: > 0 }
            ? item.Metadata.Select(kvp => new MetadataItem(kvp.Key, kvp.Value)).ToList()
            : null;

        return new WasmSearchItem(
            item.Id.Value,
            item.SourceId,
            item.Title,
            ParseMediaTypeForWasm(item.MediaType),
            item.ThumbnailUrl,
            item.Description,
            metadata);
    }

    private static WasmQueryArgs ToWasmQueryArgs(PluginSearchQuery query)
    {
        var filters = query.Filters.Count == 0
            ? null
            : query.Filters.Select(static filter => new WasmSearchFilterArg(
                filter.Id,
                filter.Values,
                filter.Operation)).ToArray();

        var additions = query.QueryAdditions.Count == 0
            ? null
            : query.QueryAdditions.Select(static addition => new WasmSearchQueryAdditionArg(
                addition.Id,
                addition.Value,
                addition.Type)).ToArray();

        return new WasmQueryArgs(
            query.Query,
            query.MediaTypes.Count == 0 ? null : query.MediaTypes,
            filters,
            additions,
            query.Sort,
            query.Page,
            query.PageSize);
    }

    /// <summary>
    /// Gets the available video streams for a media item.
    /// </summary>
    public async Task<IReadOnlyList<WasmVideoStreamItem>> GetVideoStreamsAsync(
        PluginRecord record,
        MediaId mediaId,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        string streamsJson;
        try
        {
            streamsJson = await RunInvokeOperationAsync(
                componentPath,
                operation: VideoStreamsOperation,
                mediaId: mediaId.Value,
                mediaType: VideoMediaType,
                argsJson: null,
                permittedDomains: record.Manifest.Permissions?.Domains,
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("unsupported-operation:video-streams", StringComparison.OrdinalIgnoreCase))
        {
            // Some plugins only implement paged operations; treat missing video-streams as no streams.
            return [];
        }

        var streams = DeserializeJson<IReadOnlyList<WasmVideoStreamItem>>(streamsJson);
        if (streams is null || streams.Count == 0)
        {
            return [];
        }

        return streams;
    }

    /// <summary>
    /// Gets a single video segment for a stream.
    /// </summary>
    public async Task<WasmVideoSegmentResult?> GetVideoSegmentAsync(
        PluginRecord record,
        MediaId mediaId,
        string streamId,
        int sequence,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var segmentJson = await RunInvokeOperationAsync(
            componentPath,
            operation: VideoSegmentOperation,
            mediaId: mediaId.Value,
            mediaType: VideoMediaType,
            argsJson: SerializeJson(new WasmVideoSegmentArgs(streamId, sequence)),
            permittedDomains: record.Manifest.Permissions?.Domains,
            cancellationToken: cancellationToken);

        var segment = DeserializeJson<WasmVideoSegmentWire>(segmentJson);
        if (segment is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(segment.PayloadBase64))
        {
            return new WasmVideoSegmentResult(
                string.IsNullOrWhiteSpace(segment.ContentType) ? "application/octet-stream" : segment.ContentType,
                []);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(segment.PayloadBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("WASM video segment payload is not valid base64.", ex);
        }

        return new WasmVideoSegmentResult(
            string.IsNullOrWhiteSpace(segment.ContentType) ? "application/octet-stream" : segment.ContentType,
            bytes);
    }

    /// <summary>
    /// Gets a single page for a chapter.
    /// </summary>
    public async Task<MediaPage> GetPageAsync(
        PluginRecord record,
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var invokeStopwatch = Stopwatch.StartNew();
        var pageJson = await RunInvokeOperationAsync(
            componentPath,
            operation: PageOperation,
            mediaId: mediaId.Value,
            mediaType: PagedMediaType,
            argsJson: SerializeJson(new WasmPageArgs(chapterId, pageIndex)),
            permittedDomains: record.Manifest.Permissions?.Domains,
            cancellationToken: cancellationToken);
        invokeStopwatch.Stop();

        var deserializeStopwatch = Stopwatch.StartNew();
        var page = DeserializeJson<WasmPageItem>(pageJson);
        deserializeStopwatch.Stop();

        var totalMs = invokeStopwatch.ElapsedMilliseconds + deserializeStopwatch.ElapsedMilliseconds;
        if (totalMs >= 500)
        {
            _logger.LogInformation(
                "WASM page timings for {PluginId}: invoke={InvokeMs}ms deserialize={DeserializeMs}ms total={TotalMs}ms (chapter={ChapterId}, index={PageIndex})",
                record.Manifest.Id,
                invokeStopwatch.ElapsedMilliseconds,
                deserializeStopwatch.ElapsedMilliseconds,
                totalMs,
                chapterId,
                pageIndex);
        }

        if (page is null || !Uri.TryCreate(page.ContentUri, UriKind.Absolute, out var contentUri))
        {
            throw new KeyNotFoundException($"PAGE_NOT_FOUND:{chapterId}:{pageIndex}");
        }

        return new MediaPage(page.Id, page.Index, contentUri);
    }

    /// <summary>
    /// Gets a batch of pages for a chapter, falling back to single-page requests when needed.
    /// </summary>
    public async Task<MediaPagesResult> GetPagesAsync(
        PluginRecord record,
        MediaId mediaId,
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        if (startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var batched = await TryGetPagesBatchAsync(
            record,
            mediaId,
            chapterId,
            startIndex,
            count,
            cancellationToken);
        if (batched is not null)
        {
            return batched;
        }

        var pages = new List<MediaPage>(count);
        var reachedEnd = false;

        for (var offset = 0; offset < count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageIndex = startIndex + offset;

            try
            {
                var page = await GetPageAsync(record, mediaId, chapterId, pageIndex, cancellationToken);
                pages.Add(page);
            }
            catch (KeyNotFoundException ex) when (ex.Message.StartsWith("PAGE_NOT_FOUND:", StringComparison.Ordinal))
            {
                reachedEnd = true;
                break;
            }
        }

        return new MediaPagesResult(pages, reachedEnd);
    }

    private async Task<MediaPagesResult?> TryGetPagesBatchAsync(
        PluginRecord record,
        MediaId mediaId,
        string chapterId,
        int startIndex,
        int count,
        CancellationToken cancellationToken)
    {
        if (_batchPagesBackoffUntilByPlugin.TryGetValue(record.Manifest.Id, out var backoffUntil))
        {
            if (DateTimeOffset.UtcNow < backoffUntil)
            {
                return null;
            }

            _batchPagesBackoffUntilByPlugin.TryRemove(record.Manifest.Id, out _);
        }

        var componentPath = ResolveComponentPath(record.Manifest);
        var invokeStopwatch = Stopwatch.StartNew();

        try
        {
            var pagesJson = await RunInvokeOperationAsync(
                componentPath,
                operation: PagesOperation,
                mediaId: mediaId.Value,
                mediaType: PagedMediaType,
                argsJson: SerializeJson(new WasmPagesArgs(chapterId, startIndex, count)),
                permittedDomains: record.Manifest.Permissions?.Domains,
                cancellationToken: cancellationToken);
            invokeStopwatch.Stop();

            var deserializeStopwatch = Stopwatch.StartNew();
            var pageItems = DeserializeJson<IReadOnlyList<WasmPageItem>>(pagesJson);
            deserializeStopwatch.Stop();

            var totalMs = invokeStopwatch.ElapsedMilliseconds + deserializeStopwatch.ElapsedMilliseconds;
            if (totalMs >= 500)
            {
                _logger.LogInformation(
                    "WASM pages timings for {PluginId}: invoke={InvokeMs}ms deserialize={DeserializeMs}ms total={TotalMs}ms (start={StartIndex}, count={Count})",
                    record.Manifest.Id,
                    invokeStopwatch.ElapsedMilliseconds,
                    deserializeStopwatch.ElapsedMilliseconds,
                    totalMs,
                    startIndex,
                    count);
            }

            if (pageItems is null || pageItems.Count == 0)
            {
                return null;
            }

            var pages = new List<MediaPage>(pageItems.Count);
            foreach (var item in pageItems)
            {
                if (!Uri.TryCreate(item.ContentUri, UriKind.Absolute, out var contentUri))
                {
                    return null;
                }

                pages.Add(new MediaPage(item.Id, item.Index, contentUri));
            }

            var reachedEnd = pageItems.Count < count;
            return new MediaPagesResult(pages, reachedEnd);
        }
        catch (Exception ex)
        {
            invokeStopwatch.Stop();
            _batchPagesBackoffUntilByPlugin[record.Manifest.Id] = DateTimeOffset.UtcNow.Add(BatchPagesRetryBackoff);
            _logger.LogWarning(
                ex,
                "WASM batch pages failed for plugin {PluginId}; backing off for {BackoffSeconds}s then retrying. Timings: invoke={InvokeMs}ms (start={StartIndex}, count={Count})",
                record.Manifest.Id,
                (int)BatchPagesRetryBackoff.TotalSeconds,
                invokeStopwatch.ElapsedMilliseconds,
                startIndex,
                count);
            return null;
        }
    }

    private string ResolveComponentPath(PluginManifest manifest)
    {
        if (_componentPathByPlugin.TryGetValue(manifest.Id, out var cached)
            && !string.IsNullOrWhiteSpace(cached)
            && File.Exists(cached))
        {
            return cached;
        }

        if (_entrypointResolver.TryResolveWasmComponent(manifest, out var componentPath))
        {
            _componentPathByPlugin[manifest.Id] = componentPath;
            return componentPath;
        }

        throw new InvalidOperationException($"WASM component not found for plugin '{manifest.Id}'.");
    }

    private async Task<string> RunComponentAsync(
        string componentPath,
        string operation,
        IReadOnlyList<string> operationArgs,
        IReadOnlyList<string>? permittedDomains,
        CancellationToken cancellationToken)
    {
        var pluginId = InferPluginIdFromComponentPath(componentPath);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await _invoker.InvokeAsync(componentPath, operation, operationArgs, permittedDomains, cancellationToken);
            _metrics.RecordWasmOperation(pluginId, operation, EmmaTelemetry.Outcomes.Ok, stopwatch.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            _metrics.RecordWasmOperation(pluginId, operation, EmmaTelemetry.Outcomes.Cancelled, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _metrics.RecordWasmOperation(pluginId, operation, EmmaTelemetry.Outcomes.Error, stopwatch.Elapsed.TotalMilliseconds);
            _logger.LogWarning(ex, "WASM operation {Operation} failed for {ComponentPath}", operation, componentPath);
            throw;
        }
    }

    private static string InferPluginIdFromComponentPath(string componentPath)
    {
        if (string.IsNullOrWhiteSpace(componentPath))
        {
            return "unknown";
        }

        try
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(componentPath));
            return string.IsNullOrWhiteSpace(parent) ? "unknown" : parent;
        }
        catch
        {
            return "unknown";
        }
    }

    private async Task<string> RunInvokeOperationAsync(
        string componentPath,
        string operation,
        string? mediaId,
        string? mediaType,
        string? argsJson,
        IReadOnlyList<string>? permittedDomains,
        CancellationToken cancellationToken)
    {
        var operationResult = await RunInvokeOperationWithResultAsync(
            componentPath,
            operation,
            mediaId,
            mediaType,
            argsJson,
            permittedDomains,
            cancellationToken);

        return operationResult.PayloadJson ?? string.Empty;
    }

    private async Task<WasmOperationResult> RunInvokeOperationWithResultAsync(
        string componentPath,
        string operation,
        string? mediaId,
        string? mediaType,
        string? argsJson,
        IReadOnlyList<string>? permittedDomains,
        CancellationToken cancellationToken)
    {
        var invokeArgs = new List<string>(4)
        {
            operation,
            mediaId ?? string.Empty,
            mediaType ?? string.Empty,
            argsJson ?? string.Empty
        };

        var invokeJson = await RunComponentAsync(
            componentPath,
            InvokeOperation,
            invokeArgs,
            permittedDomains,
            cancellationToken);

        var operationResult = DeserializeJson<WasmOperationResult>(invokeJson);
        if (operationResult is null)
        {
            throw new InvalidOperationException("WASM invoke result is invalid.");
        }

        if (operationResult.IsError)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(operationResult.Error)
                    ? $"WASM invoke failed for operation '{operation}'."
                    : operationResult.Error);
        }

        return operationResult;
    }

    private static bool TryParseSearchTimingMetadata(string? contentType, out SearchSplitTiming timing)
    {
        timing = default;

        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var parts = contentType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
        {
            return false;
        }

        long fetchMs = 0;
        long parseMs = 0;
        long mapMs = 0;
        long totalMs = 0;
        int resultCount = 0;
        var payloadSource = "unknown";

        foreach (var part in parts.Skip(1))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator >= part.Length - 1)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();

            switch (key)
            {
                case "emma-search-fetch-ms":
                    long.TryParse(value, out fetchMs);
                    break;
                case "emma-search-parse-ms":
                    long.TryParse(value, out parseMs);
                    break;
                case "emma-search-map-ms":
                    long.TryParse(value, out mapMs);
                    break;
                case "emma-search-total-ms":
                    long.TryParse(value, out totalMs);
                    break;
                case "emma-search-result-count":
                    int.TryParse(value, out resultCount);
                    break;
                case "emma-search-payload-source":
                    payloadSource = value;
                    break;
            }
        }

        if (fetchMs == 0 && parseMs == 0 && mapMs == 0 && totalMs == 0 && resultCount == 0 && string.Equals(payloadSource, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        timing = new SearchSplitTiming(fetchMs, parseMs, mapMs, totalMs, resultCount, payloadSource);
        return true;
    }

    private T? DeserializeJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        if (TryDeserialize(json, out T? parsed))
        {
            return parsed;
        }

        var lines = json
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse();

        foreach (var line in lines)
        {
            if (!LooksLikeJson(line))
            {
                continue;
            }

            if (TryDeserialize(line, out parsed))
            {
                return parsed;
            }
        }

        return default;
    }

    private bool TryDeserialize<T>(string json, out T? value)
    {
        try
        {
            var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>?)WasmResponseJsonContext.Default.GetTypeInfo(typeof(T));
            if (typeInfo == null)
            {
                _logger.LogWarning("No JSON type info for {Type} in WasmResponseJsonContext", typeof(T).Name);
                value = default;
                return false;
            }

            value = JsonSerializer.Deserialize(json, typeInfo);
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialization failed for type {Type}: {Message}", typeof(T).Name, ex.Message);
            value = default;
            return false;
        }
    }

    private string SerializeJson<T>(T value)
    {
        var typeInfo = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>?)WasmResponseJsonContext.Default.GetTypeInfo(typeof(T));
        if (typeInfo == null)
        {
            throw new InvalidOperationException($"No JSON type info for {typeof(T).Name} in WasmResponseJsonContext");
        }

        return JsonSerializer.Serialize(value, typeInfo);
    }

    private static bool LooksLikeJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith('"');
    }

    private WasmQueryArgs BuildSearchArgs(PluginManifest manifest, string query)
    {
        var mediaTypes = manifest.MediaTypes?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var search = manifest.SearchExperience;
        if (search is null)
        {
            return new WasmQueryArgs(query, mediaTypes);
        }

        if (LooksLikeJson(query) && TryDeserialize(query, out WasmQueryArgs? parsedArgs) && parsedArgs is not null)
        {
            var parsedMediaTypes = parsedArgs.MediaTypes is { Count: > 0 }
                ? parsedArgs.MediaTypes
                : mediaTypes;

            return parsedArgs with
            {
                Query = parsedArgs.Query?.Trim() ?? string.Empty,
                MediaTypes = parsedMediaTypes
            };
        }

        var filters = (search.Filters ?? [])
            .Select(filter =>
            {
                var values = new List<string>();
                if (filter.DefaultValues is { Count: > 0 })
                {
                    values.AddRange(filter.DefaultValues.Where(value => !string.IsNullOrWhiteSpace(value)));
                }
                else if (!string.IsNullOrWhiteSpace(filter.DefaultValue))
                {
                    values.Add(filter.DefaultValue.Trim());
                }

                if (values.Count == 0)
                {
                    return null;
                }

                return new WasmSearchFilterArg(filter.Id, values, null);
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        var additions = (search.Query?.Additions ?? [])
            .Select(addition =>
            {
                if (string.IsNullOrWhiteSpace(addition.DefaultValue))
                {
                    return null;
                }

                return new WasmSearchQueryAdditionArg(addition.Id, addition.DefaultValue.Trim(), addition.Type);
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        return new WasmQueryArgs(
            query,
            mediaTypes,
            filters.Count == 0 ? null : filters,
            additions.Count == 0 ? null : additions,
            Sort: null,
            Page: null,
            PageSize: null);
    }

    private readonly record struct SearchSplitTiming(
        long FetchMs,
        long ParseMs,
        long MapMs,
        long TotalMs,
        int ResultCount,
        string PayloadSource);
}
