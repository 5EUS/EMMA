using System.Collections.Concurrent;
using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

/// <summary>
/// Paged media pipeline endpoints backed by plugin gRPC ports.
/// </summary>
public static class PagedPipelineEndpoints
{
    private static readonly ConcurrentDictionary<string, ICachePort> _metadataCaches = new(StringComparer.OrdinalIgnoreCase);

    public static WebApplication MapPagedPipelineEndpoints(this WebApplication app)
    {
        app.MapGet("/pipeline/paged/search", async (
            string? query,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
            if (error is not null || record is null)
            {
                return error is null
                    ? Results.Problem("Plugin resolution failed.")
                    : Results.Problem(detail: error.Message, statusCode: error.StatusCode);
            }

            var isWasm = wasmRuntimeHost.IsWasmPlugin(record.Manifest);
            if (!isWasm && address is null)
            {
                return Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            IReadOnlyList<MediaSummary> results;
            if (isWasm)
            {
                var wasmLogger = loggerFactory.CreateLogger("WasmSearchEndpoint");
                var timeoutSeconds = Math.Max(1, options.Value.WasmOperationTimeoutSeconds);
                var wasmTimeout = TimeSpan.FromSeconds(timeoutSeconds);
                using var wasmTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wasmTimeoutCts.CancelAfter(wasmTimeout);

                try
                {
                    var searchTask = wasmRuntimeHost.SearchAsync(record, query ?? string.Empty, wasmTimeoutCts.Token);
                    var completed = await Task.WhenAny(searchTask, Task.Delay(wasmTimeout, cancellationToken));
                    if (completed != searchTask)
                    {
                        return Results.Problem(
                            detail: $"WASM search timed out after {timeoutSeconds}s.",
                            statusCode: StatusCodes.Status504GatewayTimeout);
                    }

                    results = await searchTask;
                }
                catch (TimeoutException ex)
                {
                    if (wasmLogger.IsEnabled(LogLevel.Warning))
                    {
                        wasmLogger.LogWarning(ex, "WASM search timed out for plugin {PluginId}", record.Manifest.Id);
                    }

                    return Results.Problem(
                        detail: ex.Message,
                        statusCode: StatusCodes.Status504GatewayTimeout);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Results.Problem(
                        detail: $"WASM search timed out after {timeoutSeconds}s.",
                        statusCode: StatusCodes.Status504GatewayTimeout);
                }
                catch (Exception ex)
                {
                    if (wasmLogger.IsEnabled(LogLevel.Error))
                    {
                        wasmLogger.LogError(ex, "WASM search failed for plugin {PluginId}", record.Manifest.Id);
                    }

                    return Results.Problem(
                        detail: $"WASM search failed: {ex.Message}",
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }
            else
            {
                var correlationId = PluginGrpcHelpers.CreateCorrelationId();
                var pipeline = CreatePipeline(
                    record,
                    address!,
                    options,
                    catalog,
                    pageAssetCache,
                    pageAssetFetcher,
                    loggerFactory,
                    correlationId);
                results = await pipeline.SearchAsync(query ?? string.Empty, cancellationToken);
            }

            return Results.Ok(results.Select(result => new
            {
                Id = result.Id.ToString(),
                Source = result.SourceId,
                result.Title,
                MediaType = result.MediaType.ToString().ToLowerInvariant(),
                ThumbnailUrl = result.ThumbnailUrl,
                Description = result.Description
            }));
        });

        app.MapGet("/pipeline/paged/chapters", async (
            string? mediaId,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                return Results.BadRequest(new { message = "mediaId is required." });
            }

            var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
            if (error is not null || record is null)
            {
                return error is null
                    ? Results.Problem("Plugin resolution failed.")
                    : Results.Problem(detail: error.Message, statusCode: error.StatusCode);
            }

            var isWasm = wasmRuntimeHost.IsWasmPlugin(record.Manifest);
            if (!isWasm && address is null)
            {
                return Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            IReadOnlyList<MediaChapter> chapters;
            if (isWasm)
            {
                chapters = await wasmRuntimeHost.GetChaptersAsync(record, MediaId.Create(mediaId), cancellationToken);
            }
            else
            {
                var correlationId = PluginGrpcHelpers.CreateCorrelationId();
                var pipeline = CreatePipeline(
                    record,
                    address!,
                    options,
                    catalog,
                    pageAssetCache,
                    pageAssetFetcher,
                    loggerFactory,
                    correlationId);
                chapters = await pipeline.GetChaptersAsync(MediaId.Create(mediaId), cancellationToken);
            }

            return Results.Ok(chapters.Select(chapter => new
            {
                Id = chapter.ChapterId,
                chapter.Number,
                chapter.Title,
                UploaderGroups = chapter.UploaderGroups ?? []
            }));
        });

        app.MapGet("/pipeline/paged/page", async (
            string? mediaId,
            string? chapterId,
            int? index,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                return Results.BadRequest(new { message = "mediaId and chapterId are required." });
            }

            var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
            if (error is not null || record is null)
            {
                return error is null
                    ? Results.Problem("Plugin resolution failed.")
                    : Results.Problem(detail: error.Message, statusCode: error.StatusCode);
            }

            var isWasm = wasmRuntimeHost.IsWasmPlugin(record.Manifest);
            if (!isWasm && address is null)
            {
                return Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            MediaPage page;
            if (isWasm)
            {
                page = await wasmRuntimeHost.GetPageAsync(
                    record,
                    MediaId.Create(mediaId),
                    chapterId,
                    index ?? 0,
                    cancellationToken);
            }
            else
            {
                var correlationId = PluginGrpcHelpers.CreateCorrelationId();
                var pipeline = CreatePipeline(
                    record,
                    address!,
                    options,
                    catalog,
                    pageAssetCache,
                    pageAssetFetcher,
                    loggerFactory,
                    correlationId);
                page = await pipeline.GetPageAsync(
                    MediaId.Create(mediaId),
                    chapterId,
                    index ?? 0,
                    cancellationToken);
            }

            return Results.Ok(new
            {
                Id = page.PageId,
                page.Index,
                ContentUri = page.ContentUri.ToString()
            });
        });

        app.MapGet("/pipeline/paged/pages", async (
            string? mediaId,
            string? chapterId,
            int? startIndex,
            int? count,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                return Results.BadRequest(new { message = "mediaId and chapterId are required." });
            }

            var safeStartIndex = Math.Max(0, startIndex ?? 0);
            var safeCount = Math.Max(1, count ?? 1);

            var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
            if (error is not null || record is null)
            {
                return error is null
                    ? Results.Problem("Plugin resolution failed.")
                    : Results.Problem(detail: error.Message, statusCode: error.StatusCode);
            }

            var isWasm = wasmRuntimeHost.IsWasmPlugin(record.Manifest);
            if (!isWasm && address is null)
            {
                return Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            MediaPagesResult pages;
            if (isWasm)
            {
                pages = await wasmRuntimeHost.GetPagesAsync(
                    record,
                    MediaId.Create(mediaId),
                    chapterId,
                    safeStartIndex,
                    safeCount,
                    cancellationToken);
            }
            else
            {
                var correlationId = PluginGrpcHelpers.CreateCorrelationId();
                var pipeline = CreatePipeline(
                    record,
                    address!,
                    options,
                    catalog,
                    pageAssetCache,
                    pageAssetFetcher,
                    loggerFactory,
                    correlationId);
                pages = await pipeline.GetPagesAsync(
                    MediaId.Create(mediaId),
                    chapterId,
                    safeStartIndex,
                    safeCount,
                    cancellationToken);
            }

            return Results.Ok(new
            {
                Pages = pages.Pages.Select(page => new
                {
                    Id = page.PageId,
                    page.Index,
                    ContentUri = page.ContentUri.ToString()
                }),
                pages.ReachedEnd
            });
        });

        app.MapGet("/pipeline/paged/page-asset", async (
            string? mediaId,
            string? chapterId,
            int? index,
            string? pluginId,
            PluginResolutionService pluginResolution,
            IWasmPluginRuntimeHost wasmRuntimeHost,
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                return Results.BadRequest(new { message = "mediaId and chapterId are required." });
            }

            var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
            if (error is not null || record is null)
            {
                return error is null
                    ? Results.Problem("Plugin resolution failed.")
                    : Results.Problem(detail: error.Message, statusCode: error.StatusCode);
            }

            var isWasm = wasmRuntimeHost.IsWasmPlugin(record.Manifest);
            if (!isWasm && address is null)
            {
                return Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            MediaPage page;
            if (isWasm)
            {
                page = await wasmRuntimeHost.GetPageAsync(
                    record,
                    MediaId.Create(mediaId),
                    chapterId,
                    index ?? 0,
                    cancellationToken);
            }
            else
            {
                var correlationId = PluginGrpcHelpers.CreateCorrelationId();
                var pipeline = CreatePipeline(
                    record,
                    address!,
                    options,
                    catalog,
                    pageAssetCache,
                    pageAssetFetcher,
                    loggerFactory,
                    correlationId);
                page = await pipeline.GetPageAsync(
                    MediaId.Create(mediaId),
                    chapterId,
                    index ?? 0,
                    cancellationToken);
            }

            var cacheKey = $"{record.Manifest.Id}:{page.PageId}:{page.ContentUri}";
            var asset = await pageAssetCache.GetAsync(cacheKey, cancellationToken);
            if (asset is null)
            {
                asset = await pageAssetFetcher.FetchAsync(page.ContentUri, cancellationToken);
                await pageAssetCache.SetAsync(cacheKey, asset, cancellationToken);
            }

            return Results.File(asset.Payload, asset.ContentType);
        });

        return app;
    }

    private static PagedMediaPipeline CreatePipeline(
        PluginRecord record,
        Uri address,
        IOptions<PluginHostOptions> options,
        IMediaCatalogPort catalog,
        IPageAssetCachePort pageAssetCache,
        IPageAssetFetcherPort pageAssetFetcher,
        ILoggerFactory loggerFactory,
        string correlationId)
    {
        var endpoint = new PluginGrpcEndpoint(record, address, correlationId);
        var searchPort = new PluginSearchPort(
            endpoint,
            options,
            loggerFactory.CreateLogger<PluginSearchPort>());
        var pagePort = new PluginPageProviderPort(
            endpoint,
            options,
            loggerFactory.CreateLogger<PluginPageProviderPort>());

        var timeoutSeconds = Math.Max(1, options.Value.ProbeTimeoutSeconds);
        var pipelineOptions = new PagedMediaPipelineOptions(
            TimeSpan.FromSeconds(timeoutSeconds),
            1,
            TimeSpan.FromMilliseconds(200));

        var cache = _metadataCaches.GetOrAdd(record.Manifest.Id, _ => new InMemoryCachePort());

        return new PagedMediaPipeline(
            searchPort,
            pagePort,
            new ManifestPolicyEvaluator(ManifestPolicyMapping.ToDefinition(record.Manifest)),
            cache,
            pipelineOptions,
            pageAssetCache,
            pageAssetFetcher,
            catalog);
    }

}
