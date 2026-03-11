using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using EMMA.Domain;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

public interface IWasmPluginRuntimeHost
{
    bool IsWasmPlugin(PluginManifest manifest);
    Task WarmupAsync(PluginManifest manifest, CancellationToken cancellationToken);
    Task<PluginHandshakeStatus> HandshakeAsync(PluginManifest manifest, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaSummary>> SearchAsync(PluginRecord record, string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(PluginRecord record, MediaId mediaId, CancellationToken cancellationToken);
    Task<MediaPage> GetPageAsync(PluginRecord record, MediaId mediaId, string chapterId, int pageIndex, CancellationToken cancellationToken);
    Task<MediaPagesResult> GetPagesAsync(PluginRecord record, MediaId mediaId, string chapterId, int startIndex, int count, CancellationToken cancellationToken);
}

public interface IWasmComponentInvoker
{
    Task<string> InvokeAsync(
        string componentPath,
        string operation,
        IReadOnlyList<string> operationArgs,
        CancellationToken cancellationToken);
}

public sealed class WasmPluginRuntimeHost(
    IPluginEntrypointResolver entrypointResolver,
    IWasmComponentInvoker invoker,
    IOptions<PluginHostOptions> options,
    ILogger<WasmPluginRuntimeHost> logger) : IWasmPluginRuntimeHost
{
    private const string HandshakeOperation = "handshake";
    private const string CapabilitiesOperation = "capabilities";
    private const string SearchOperation = "search";
    private const string ChaptersOperation = "chapters";
    private const string PageOperation = "page";
    private const string PagesOperation = "pages";

    private readonly IPluginEntrypointResolver _entrypointResolver = entrypointResolver;
    private readonly IWasmComponentInvoker _invoker = invoker;
    private readonly PluginHostOptions _options = options.Value;
    private readonly ILogger<WasmPluginRuntimeHost> _logger = logger;
    private readonly ConcurrentDictionary<string, BridgePayloadCacheEntry> _bridgePayloadCache = new();
    private readonly ConcurrentDictionary<string, HttpClient> _bridgeHttpClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _batchPagesBackoffUntilByPlugin = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SearchCacheEntry> _searchCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _warmupByPlugin = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _componentPathByPlugin = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan BridgePayloadCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WarmupTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SearchBridgeFetchBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BatchPagesRetryBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBridgeRetryDelay = TimeSpan.FromSeconds(2);
    private const string InlineBridgePayloadPrefix = "emma-inline-json-b64:";
    private const int InlineBridgePayloadArgHardLimitBytes = 48 * 1024;

    private readonly record struct BridgePayloadCacheEntry(string Payload, DateTimeOffset FetchedAtUtc);
    private readonly record struct SearchCacheEntry(IReadOnlyList<MediaSummary> Results, DateTimeOffset CachedAtUtc);

    public bool IsWasmPlugin(PluginManifest manifest)
    {
        return _entrypointResolver.TryResolveWasmComponent(manifest, out _);
    }

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
            await RunComponentAsync(componentPath, CapabilitiesOperation, [], cancellationToken);
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

        var handshakeJson = await RunComponentAsync(componentPath, HandshakeOperation, [], cancellationToken);
        var health = DeserializeJson<WasmHealth>(handshakeJson);
        var capabilitiesJson = await RunComponentAsync(componentPath, CapabilitiesOperation, [], cancellationToken);
        var capabilities = DeserializeJson<IReadOnlyList<string>>(capabilitiesJson) ?? [];

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

        var componentPath = ResolveComponentPath(record.Manifest);
        var args = new List<string>
        {
            normalizedQuery
        };

        var directAttempted = false;
        var directFailedOrEmpty = false;

        var hasBridgeSearch = TryResolveHttpBridgeOperation(record.Manifest, SearchOperation, out _);
        if (_options.WasmDirectHttp && !hasBridgeSearch)
        {
            directAttempted = true;
            try
            {
                var directInvokeStopwatch = Stopwatch.StartNew();
                var directSearchJson = await RunComponentAsync(componentPath, SearchOperation, args, cancellationToken);
                directInvokeStopwatch.Stop();

                var directDeserializeStopwatch = Stopwatch.StartNew();
                var directSearchItems = DeserializeJson<IReadOnlyList<WasmSearchItem>>(directSearchJson);
                directDeserializeStopwatch.Stop();

                if (directSearchItems is not null && directSearchItems.Count > 0)
                {
                    var directMappedResults = directSearchItems.Select(item => new MediaSummary(
                        MediaId.Create(item.Id),
                        item.Source ?? record.Manifest.Id,
                        item.Title,
                        string.Equals(item.MediaType, "video", StringComparison.OrdinalIgnoreCase)
                            ? MediaType.Video
                            : MediaType.Paged,
                        string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
                        string.IsNullOrWhiteSpace(item.Description) ? null : item.Description))
                        .ToArray();

                    _searchCache[searchCacheKey] = new SearchCacheEntry(directMappedResults, DateTimeOffset.UtcNow);

                    _logger.LogInformation(
                        "WASM search direct HTTP succeeded for {PluginId}: invoke={InvokeMs}ms deserialize={DeserializeMs}ms queryLength={QueryLength}",
                        record.Manifest.Id,
                        directInvokeStopwatch.ElapsedMilliseconds,
                        directDeserializeStopwatch.ElapsedMilliseconds,
                        normalizedQuery.Length);

                    return directMappedResults;
                }

                _logger.LogWarning(
                    "WASM search direct HTTP returned no results for {PluginId}; falling back to host bridge.",
                    record.Manifest.Id);
                directFailedOrEmpty = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "WASM search direct HTTP failed for {PluginId}; falling back to host bridge.",
                    record.Manifest.Id);
                directFailedOrEmpty = true;
            }
        }
        else if (_options.WasmDirectHttp && hasBridgeSearch)
        {
            _logger.LogDebug(
                "WASM direct HTTP search path is disabled for {PluginId} because host bridge search is configured.",
                record.Manifest.Id);
        }

        var enrichStopwatch = Stopwatch.StartNew();
        args = await EnrichOperationArgsAsync(record.Manifest, componentPath, SearchOperation, args, cancellationToken);
        enrichStopwatch.Stop();

        if (directAttempted && directFailedOrEmpty && args.Count <= 1)
        {
            throw new InvalidOperationException(
                $"WASM search bridge payload unavailable for '{record.Manifest.Id}' after direct HTTP fallback.");
        }

        if (hasBridgeSearch && args.Count <= 1)
        {
            throw new InvalidOperationException(
                $"WASM search bridge payload unavailable for '{record.Manifest.Id}'.");
        }

        var invokeStopwatch = Stopwatch.StartNew();
        var searchJson = await RunComponentAsync(componentPath, SearchOperation, args, cancellationToken);
        invokeStopwatch.Stop();

        var deserializeStopwatch = Stopwatch.StartNew();
        var searchItems = DeserializeJson<IReadOnlyList<WasmSearchItem>>(searchJson);
        deserializeStopwatch.Stop();

        var totalMs = enrichStopwatch.ElapsedMilliseconds
            + invokeStopwatch.ElapsedMilliseconds
            + deserializeStopwatch.ElapsedMilliseconds;
        if (totalMs >= 500)
        {
            _logger.LogInformation(
                "WASM search timings for {PluginId}: bridge/enrich={EnrichMs}ms invoke={InvokeMs}ms deserialize={DeserializeMs}ms total={TotalMs}ms (queryLength={QueryLength})",
                record.Manifest.Id,
                enrichStopwatch.ElapsedMilliseconds,
                invokeStopwatch.ElapsedMilliseconds,
                deserializeStopwatch.ElapsedMilliseconds,
                totalMs,
                normalizedQuery.Length);
        }
        else
        {
            _logger.LogDebug(
                "WASM search timings for {PluginId}: bridge/enrich={EnrichMs}ms invoke={InvokeMs}ms deserialize={DeserializeMs}ms total={TotalMs}ms (queryLength={QueryLength})",
                record.Manifest.Id,
                enrichStopwatch.ElapsedMilliseconds,
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

        var mappedResults = searchItems.Select(item => new MediaSummary(
            MediaId.Create(item.Id),
            item.Source ?? record.Manifest.Id,
            item.Title,
            string.Equals(item.MediaType, "video", StringComparison.OrdinalIgnoreCase)
                ? MediaType.Video
                : MediaType.Paged,
            string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? null : item.ThumbnailUrl,
            string.IsNullOrWhiteSpace(item.Description) ? null : item.Description))
            .ToArray();

        _searchCache[searchCacheKey] = new SearchCacheEntry(mappedResults, DateTimeOffset.UtcNow);

        return mappedResults;
    }

    public async Task<IReadOnlyList<MediaChapter>> GetChaptersAsync(
        PluginRecord record,
        MediaId mediaId,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var args = new List<string>
        {
            mediaId.Value
        };

        if (_options.WasmDirectHttp)
        {
            try
            {
                var directChaptersJson = await RunComponentAsync(componentPath, ChaptersOperation, args, cancellationToken);
                var directChapters = DeserializeJson<IReadOnlyList<WasmChapterItem>>(directChaptersJson);
                if (directChapters is not null && directChapters.Count > 0)
                {
                    _logger.LogDebug(
                        "WASM chapters direct HTTP succeeded for {PluginId}.",
                        record.Manifest.Id);
                    return [.. directChapters.Select(chapter => new MediaChapter(chapter.Id, chapter.Number, chapter.Title))];
                }

                _logger.LogWarning(
                    "WASM chapters direct HTTP returned no data for {PluginId}; falling back to host bridge.",
                    record.Manifest.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "WASM chapters direct HTTP failed for {PluginId}; falling back to host bridge.",
                    record.Manifest.Id);
            }
        }

        args = await EnrichOperationArgsAsync(record.Manifest, componentPath, ChaptersOperation, args, cancellationToken);

        var chaptersJson = await RunComponentAsync(componentPath, ChaptersOperation, args, cancellationToken);
        var chapters = DeserializeJson<IReadOnlyList<WasmChapterItem>>(chaptersJson);
        if (chapters is null || chapters.Count == 0)
        {
            return [];
        }

        return [.. chapters.Select(chapter => new MediaChapter(chapter.Id, chapter.Number, chapter.Title))];
    }

    public async Task<MediaPage> GetPageAsync(
        PluginRecord record,
        MediaId mediaId,
        string chapterId,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var componentPath = ResolveComponentPath(record.Manifest);
        var args = new List<string>
        {
            mediaId.Value,
            chapterId,
            pageIndex.ToString()
        };

        if (_options.WasmDirectHttp)
        {
            try
            {
                var directPageJson = await RunComponentAsync(
                    componentPath,
                    PageOperation,
                    args,
                    cancellationToken);
                var directPage = DeserializeJson<WasmPageItem>(directPageJson);
                if (directPage is not null && Uri.TryCreate(directPage.ContentUri, UriKind.Absolute, out var directContentUri))
                {
                    _logger.LogDebug(
                        "WASM page direct HTTP succeeded for {PluginId} (chapter={ChapterId}, index={PageIndex}).",
                        record.Manifest.Id,
                        chapterId,
                        pageIndex);
                    return new MediaPage(directPage.Id, directPage.Index, directContentUri);
                }

                _logger.LogWarning(
                    "WASM page direct HTTP returned invalid data for {PluginId} (chapter={ChapterId}, index={PageIndex}); falling back to host bridge.",
                    record.Manifest.Id,
                    chapterId,
                    pageIndex);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "WASM page direct HTTP failed for {PluginId} (chapter={ChapterId}, index={PageIndex}); falling back to host bridge.",
                    record.Manifest.Id,
                    chapterId,
                    pageIndex);
            }
        }

        var enrichStopwatch = Stopwatch.StartNew();
        args = await EnrichOperationArgsAsync(record.Manifest, componentPath, PageOperation, args, cancellationToken);
        enrichStopwatch.Stop();

        var invokeStopwatch = Stopwatch.StartNew();
        var pageJson = await RunComponentAsync(
            componentPath,
            PageOperation,
            args,
            cancellationToken);
        invokeStopwatch.Stop();

        var deserializeStopwatch = Stopwatch.StartNew();
        var page = DeserializeJson<WasmPageItem>(pageJson);
        deserializeStopwatch.Stop();

        var totalMs = enrichStopwatch.ElapsedMilliseconds
            + invokeStopwatch.ElapsedMilliseconds
            + deserializeStopwatch.ElapsedMilliseconds;
        if (totalMs >= 500)
        {
            _logger.LogInformation(
                "WASM page timings for {PluginId}: bridge/enrich={EnrichMs}ms invoke={InvokeMs}ms deserialize={DeserializeMs}ms total={TotalMs}ms (chapter={ChapterId}, index={PageIndex})",
                record.Manifest.Id,
                enrichStopwatch.ElapsedMilliseconds,
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
        var args = new List<string>
        {
            mediaId.Value,
            chapterId,
            startIndex.ToString(),
            count.ToString()
        };

        if (_options.WasmDirectHttp)
        {
            try
            {
                var directPagesJson = await RunComponentAsync(componentPath, PagesOperation, args, cancellationToken);
                var directPageItems = DeserializeJson<IReadOnlyList<WasmPageItem>>(directPagesJson);
                if (directPageItems is not null && directPageItems.Count > 0)
                {
                    var directPages = new List<MediaPage>(directPageItems.Count);
                    foreach (var item in directPageItems)
                    {
                        if (!Uri.TryCreate(item.ContentUri, UriKind.Absolute, out var contentUri))
                        {
                            directPages.Clear();
                            break;
                        }

                        directPages.Add(new MediaPage(item.Id, item.Index, contentUri));
                    }

                    if (directPages.Count > 0)
                    {
                        _logger.LogDebug(
                            "WASM pages direct HTTP succeeded for {PluginId} (start={StartIndex}, count={Count}).",
                            record.Manifest.Id,
                            startIndex,
                            count);
                        var reachedEndDirect = directPageItems.Count < count;
                        return new MediaPagesResult(directPages, reachedEndDirect);
                    }
                }

                _logger.LogWarning(
                    "WASM pages direct HTTP returned no/invalid data for {PluginId} (start={StartIndex}, count={Count}); falling back to host bridge.",
                    record.Manifest.Id,
                    startIndex,
                    count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "WASM pages direct HTTP failed for {PluginId} (start={StartIndex}, count={Count}); falling back to host bridge.",
                    record.Manifest.Id,
                    startIndex,
                    count);
            }
        }

        var enrichStopwatch = Stopwatch.StartNew();
        args = await EnrichOperationArgsAsync(record.Manifest, componentPath, PagesOperation, args, cancellationToken);
        enrichStopwatch.Stop();

        var invokeStopwatch = Stopwatch.StartNew();

        try
        {
            var pagesJson = await RunComponentAsync(componentPath, PagesOperation, args, cancellationToken);
            invokeStopwatch.Stop();

            var deserializeStopwatch = Stopwatch.StartNew();
            var pageItems = DeserializeJson<IReadOnlyList<WasmPageItem>>(pagesJson);
            deserializeStopwatch.Stop();

            var totalMs = enrichStopwatch.ElapsedMilliseconds
                + invokeStopwatch.ElapsedMilliseconds
                + deserializeStopwatch.ElapsedMilliseconds;
            if (totalMs >= 500)
            {
                _logger.LogInformation(
                    "WASM pages timings for {PluginId}: bridge/enrich={EnrichMs}ms invoke={InvokeMs}ms deserialize={DeserializeMs}ms total={TotalMs}ms (start={StartIndex}, count={Count})",
                    record.Manifest.Id,
                    enrichStopwatch.ElapsedMilliseconds,
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
                "WASM batch pages failed for plugin {PluginId}; backing off for {BackoffSeconds}s then retrying. Timings: enrich={EnrichMs}ms invoke={InvokeMs}ms (start={StartIndex}, count={Count})",
                record.Manifest.Id,
                (int)BatchPagesRetryBackoff.TotalSeconds,
                enrichStopwatch.ElapsedMilliseconds,
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
        CancellationToken cancellationToken)
    {
        try
        {
            return await _invoker.InvokeAsync(componentPath, operation, operationArgs, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WASM operation {Operation} failed for {ComponentPath}", operation, componentPath);
            throw;
        }
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
            // Use the context's GetTypeInfo to avoid reflection
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

    private static bool LooksLikeJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith('"');
    }

    private async Task<List<string>> EnrichOperationArgsAsync(
        PluginManifest manifest,
        string componentPath,
        string operation,
        List<string> operationArgs,
        CancellationToken cancellationToken)
    {
        if (!TryResolveHttpBridgeOperation(manifest, operation, out var bridgeOperation)
            || bridgeOperation is null)
        {
            return operationArgs;
        }

        string payload;
        var fetchStopwatch = Stopwatch.StartNew();
        if (string.Equals(operation, SearchOperation, StringComparison.OrdinalIgnoreCase))
        {
            using var bridgeBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bridgeBudgetCts.CancelAfter(SearchBridgeFetchBudget);

            try
            {
                payload = await FetchBridgePayloadAsync(manifest, operation, bridgeOperation, operationArgs, bridgeBudgetCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                fetchStopwatch.Stop();
                _logger.LogWarning(
                    "WASM search bridge fetch exceeded {BridgeBudgetMs}ms for {PluginId}. Continuing without bridge payload.",
                    (int)SearchBridgeFetchBudget.TotalMilliseconds,
                    manifest.Id);
                return operationArgs;
            }
            catch (Exception ex)
            {
                fetchStopwatch.Stop();
                _logger.LogWarning(
                    ex,
                    "WASM search bridge fetch failed for {PluginId}. Continuing without bridge payload.",
                    manifest.Id);
                return operationArgs;
            }
        }
        else
        {
            payload = await FetchBridgePayloadAsync(manifest, operation, bridgeOperation, operationArgs, cancellationToken);
        }
        fetchStopwatch.Stop();

        var payloadBytes = string.IsNullOrEmpty(payload)
            ? 0
            : System.Text.Encoding.UTF8.GetByteCount(payload);

        var writeStopwatch = Stopwatch.StartNew();
        var payloadArg = await BuildBridgePayloadArgAsync(componentPath, operation, payload, payloadBytes, cancellationToken);
        writeStopwatch.Stop();

        var totalMs = fetchStopwatch.ElapsedMilliseconds + writeStopwatch.ElapsedMilliseconds;
        if (totalMs >= 500)
        {
            _logger.LogInformation(
                "WASM bridge timings for {PluginId}/{Operation}: fetchMs={FetchMs} writeMs={WriteMs} payloadBytes={PayloadBytes} inMemoryEnabled={InMemoryEnabled} totalMs={TotalMs}",
                manifest.Id,
                operation,
                fetchStopwatch.ElapsedMilliseconds,
                writeStopwatch.ElapsedMilliseconds,
                payloadBytes,
                _options.WasmBridgeInMemoryPayload,
                totalMs);
        }
        else
        {
            _logger.LogDebug(
                "WASM bridge timings for {PluginId}/{Operation}: fetchMs={FetchMs} writeMs={WriteMs} payloadBytes={PayloadBytes} inMemoryEnabled={InMemoryEnabled} totalMs={TotalMs}",
                manifest.Id,
                operation,
                fetchStopwatch.ElapsedMilliseconds,
                writeStopwatch.ElapsedMilliseconds,
                payloadBytes,
                _options.WasmBridgeInMemoryPayload,
                totalMs);
        }

        operationArgs.Add(payloadArg);

        return operationArgs;
    }

    private static bool TryResolveHttpBridgeOperation(
        PluginManifest manifest,
        string operation,
        out PluginManifestWasmHttpOperation? bridgeOperation)
    {
        bridgeOperation = null;

        // TODO(dotnet-wasm-http): Remove host-supplied HTTP bridge once outbound HttpClient is supported for .NET WASM components.
        var operations = manifest.Runtime?.WasmHostBridge?.Http;
        if (operations is null || operations.Count == 0)
        {
            return false;
        }

        if (!operations.TryGetValue(operation, out bridgeOperation) || bridgeOperation is null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(bridgeOperation.UrlTemplate);
    }

    private async Task<string> FetchBridgePayloadAsync(
        PluginManifest manifest,
        string operation,
        PluginManifestWasmHttpOperation bridgeOperation,
        IReadOnlyList<string> operationArgs,
        CancellationToken cancellationToken)
    {
        var method = string.IsNullOrWhiteSpace(bridgeOperation.Method)
            ? HttpMethod.Get
            : new HttpMethod(bridgeOperation.Method.Trim().ToUpperInvariant());

        if (method != HttpMethod.Get)
        {
            throw new InvalidOperationException(
                $"Unsupported WASM host bridge HTTP method '{method}'. Only GET is currently supported.");
        }

        var resolvedUrl = ResolveUrlTemplate(bridgeOperation.UrlTemplate, operationArgs);
        var requestUri = ResolveBridgeRequestUri(manifest, resolvedUrl);
        var baseAddress = new Uri(requestUri.GetLeftPart(UriPartial.Authority));
        var requestTarget = requestUri.PathAndQuery + requestUri.Fragment;
        var cacheKey = $"{method}:{requestUri}";

        if (method == HttpMethod.Get)
        {
            var now = DateTimeOffset.UtcNow;
            if (_bridgePayloadCache.TryGetValue(cacheKey, out var cached)
                && now - cached.FetchedAtUtc <= BridgePayloadCacheTtl)
            {
                return cached.Payload;
            }
        }

        EnsureHostIsAllowed(manifest, requestUri);

        var maxAttempts = ResolveBridgeMaxAttempts(operation);
        var client = GetOrCreateHostHttpClient(baseAddress, operation);
        for (var attempt = 1; attempt <= maxAttempts; attempt++) // TODO custom number of retries
        {
            using var request = new HttpRequestMessage(method, requestTarget);
            try
            {
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (method == HttpMethod.Get && !string.IsNullOrWhiteSpace(payload))
                    {
                        _bridgePayloadCache[cacheKey] = new BridgePayloadCacheEntry(
                            payload,
                            DateTimeOffset.UtcNow);
                    }

                    return payload;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var statusCode = (int)response.StatusCode;
                var transient = IsTransientStatusCode(statusCode);

                if (transient && attempt < maxAttempts)
                {
                    var retryDelay = ResolveRetryDelay(response, attempt);
                    _logger.LogWarning(
                        "WASM host bridge transient HTTP failure {StatusCode} {ReasonPhrase} for {RequestUri}, retrying in {DelayMs}ms (attempt {Attempt}/{MaxAttempts})",
                        statusCode,
                        response.ReasonPhrase,
                        requestUri,
                        (int)retryDelay.TotalMilliseconds,
                        attempt,
                        maxAttempts);

                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                var bodyPreview = BuildResponsePreview(body);
                if (transient)
                {
                    throw new InvalidOperationException(
                        $"WASM host bridge upstream is unavailable ({statusCode} {response.ReasonPhrase}) at '{requestUri}'. Try again later. Provider response: {bodyPreview}");
                }

                throw new InvalidOperationException(
                    $"WASM host bridge HTTP request failed: {statusCode} {response.ReasonPhrase} at '{requestUri}'. Response: {bodyPreview}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                var retryDelay = TimeSpan.FromMilliseconds(250 * attempt);
                _logger.LogWarning(
                    ex,
                    "WASM host bridge HTTP transport failure for {RequestUri}, retrying in {DelayMs}ms (attempt {Attempt}/{MaxAttempts})",
                    requestUri,
                    (int)retryDelay.TotalMilliseconds,
                    attempt,
                    maxAttempts);

                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"WASM host bridge HTTP transport error for '{requestUri}': {ex.Message}",
                    ex);
            }
        }

        throw new InvalidOperationException(
            $"WASM host bridge HTTP request failed for '{requestUri}' after retries.");
    }

    private static int ResolveBridgeMaxAttempts(string operation)
    {
        if (string.Equals(operation, SearchOperation, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 3;
    }

    private static bool IsTransientStatusCode(int statusCode)
    {
        return statusCode is 429 or 502 or 503 or 504;
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta <= MaxBridgeRetryDelay ? delta : MaxBridgeRetryDelay;
        }

        var calculated = TimeSpan.FromMilliseconds(300 * attempt);
        return calculated <= MaxBridgeRetryDelay ? calculated : MaxBridgeRetryDelay;
    }

    private static string BuildResponsePreview(string body)
    {
        var normalized = body
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "<empty>";
        }

        return normalized.Length > 180
            ? normalized[..180] + "..."
            : normalized;
    }

    private static Uri ResolveBridgeRequestUri(PluginManifest manifest, string resolvedUrl)
    {
        if (Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (!Uri.TryCreate(resolvedUrl, UriKind.Relative, out var relative))
        {
            throw new InvalidOperationException(
                $"WASM host bridge URL template resolved to invalid URI: '{resolvedUrl}'.");
        }

        var baseAddress = ResolveBridgeBaseAddress(manifest);
        return new Uri(baseAddress, relative);
    }

    private static Uri ResolveBridgeBaseAddress(PluginManifest manifest)
    {
        var operations = manifest.Runtime?.WasmHostBridge?.Http;
        if (operations is not null)
        {
            foreach (var operation in operations.Values)
            {
                var template = operation?.UrlTemplate;
                if (string.IsNullOrWhiteSpace(template))
                {
                    continue;
                }

                if (!Uri.TryCreate(template, UriKind.Absolute, out var absolute))
                {
                    continue;
                }

                return new Uri(absolute.GetLeftPart(UriPartial.Authority));
            }
        }

        var firstDomain = manifest.Permissions?.Domains?
            .FirstOrDefault(domain => !string.IsNullOrWhiteSpace(domain) && domain != "*")
            ?.Trim();

        if (!string.IsNullOrWhiteSpace(firstDomain)
            && Uri.TryCreate($"https://{firstDomain}", UriKind.Absolute, out var inferred))
        {
            return inferred;
        }

        throw new InvalidOperationException(
            "WASM host bridge URL template is relative, but no absolute base address could be resolved from runtime.wasmHostBridge.http or permissions.domains.");
    }

    private async Task<string> WriteBridgePayloadAsync(
        string componentPath,
        string operation,
        string payload,
        CancellationToken cancellationToken)
    {
        // Use a writable temp directory instead of the component directory
        // (which may be inside a read-only app bundle on macOS)
        var componentHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(componentPath)))
            .ToLowerInvariant()[..16];

        var tempRoot = Path.Combine(Path.GetTempPath(), "emma-wasm-bridge");
        var bridgeDir = Path.Combine(tempRoot, componentHash, ".hostbridge");
        Directory.CreateDirectory(bridgeDir);

        var fileName = $"{operation}-{Guid.NewGuid():N}.json";
        var filePath = Path.Combine(bridgeDir, fileName);
        await File.WriteAllTextAsync(filePath, payload, cancellationToken);

        return $"/.hostbridge/{fileName}";
    }

    private async Task<string> BuildBridgePayloadArgAsync(
        string componentPath,
        string operation,
        string payload,
        int payloadBytes,
        CancellationToken cancellationToken)
    {
        var payloadText = payload ?? string.Empty;

        if (!_options.WasmBridgeInMemoryPayload)
        {
            return await WriteBridgePayloadAsync(componentPath, operation, payloadText, cancellationToken);
        }

        var payloadUtf8 = System.Text.Encoding.UTF8.GetBytes(payloadText);
        var inlineArgBytes = System.Text.Encoding.UTF8.GetByteCount(InlineBridgePayloadPrefix)
            + (((payloadUtf8.Length + 2) / 3) * 4);
        var configuredMaxBytes = Math.Max(1, _options.WasmBridgeInMemoryPayloadMaxBytes);
        var effectiveMaxBytes = Math.Min(configuredMaxBytes, InlineBridgePayloadArgHardLimitBytes);
        if (payloadBytes > configuredMaxBytes || inlineArgBytes > effectiveMaxBytes)
        {
            _logger.LogDebug(
                "WASM bridge payload exceeds in-memory max for {Operation}: payloadBytes={PayloadBytes}, inlineArgBytes={InlineArgBytes}, configuredMaxBytes={ConfiguredMaxBytes}, effectiveMaxBytes={EffectiveMaxBytes}. Falling back to file payload.",
                operation,
                payloadBytes,
                inlineArgBytes,
                configuredMaxBytes,
                effectiveMaxBytes);

            return await WriteBridgePayloadAsync(componentPath, operation, payloadText, cancellationToken);
        }

        var base64 = Convert.ToBase64String(payloadUtf8);
        return InlineBridgePayloadPrefix + base64;
    }

    private static string ResolveUrlTemplate(string template, IReadOnlyList<string> operationArgs)
    {
        var resolved = template;
        for (var index = 0; index < operationArgs.Count; index++)
        {
            resolved = resolved.Replace(
                $"{{arg{index}}}",
                Uri.EscapeDataString(operationArgs[index] ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
        }

        return resolved;
    }

    private static void EnsureHostIsAllowed(PluginManifest manifest, Uri requestUri)
    {
        var allowedDomains = manifest.Permissions?.Domains;
        if (allowedDomains is null || allowedDomains.Count == 0)
        {
            return;
        }

        var host = requestUri.Host;
        var isAllowed = allowedDomains.Any(domain =>
            !string.IsNullOrWhiteSpace(domain)
            && (string.Equals(domain, "*", StringComparison.OrdinalIgnoreCase)
                || host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase)));

        if (!isAllowed)
        {
            throw new InvalidOperationException(
                $"WASM host bridge blocked request to '{requestUri.Host}'. Domain is not listed in plugin permissions.");
        }
    }

    private static HttpClient CreateHostHttpClient(Uri baseAddress, TimeSpan timeout)
    {
        HttpMessageHandler handler = new HttpClientHandler();
        if (OperatingSystem.IsIOS())
        {
            var relaxTls = Environment.GetEnvironmentVariable("EMMA_IOS_RELAX_TLS_VALIDATION");
            var relaxEnabled = string.Equals(relaxTls, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relaxTls, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relaxTls, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relaxTls, "on", StringComparison.OrdinalIgnoreCase);

            if (relaxEnabled && handler is HttpClientHandler iosHandler)
            {
                iosHandler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = timeout
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("EMMA-PluginHost/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        return client;
    }

    private HttpClient GetOrCreateHostHttpClient(Uri baseAddress, string operation)
    {
        var authority = baseAddress.GetLeftPart(UriPartial.Authority);
        var timeout = string.Equals(operation, SearchOperation, StringComparison.OrdinalIgnoreCase)
            ? SearchBridgeFetchBudget
            : TimeSpan.FromSeconds(15);
        var timeoutMs = (int)Math.Ceiling(timeout.TotalMilliseconds);
        var key = $"{authority}|{timeoutMs}";
        return _bridgeHttpClients.GetOrAdd(key, _ => CreateHostHttpClient(baseAddress, timeout));
    }

}
