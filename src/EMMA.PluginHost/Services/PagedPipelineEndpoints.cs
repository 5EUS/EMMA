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
            PluginProcessManager processManager,
            IOptions<PluginHostOptions> options,
            IMediaCatalogPort catalog,
            IPageAssetCachePort pageAssetCache,
            IPageAssetFetcherPort pageAssetFetcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var (record, address, error) = await pluginResolution.ResolveAsync(pluginId, cancellationToken);
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            var correlationId = PluginGrpcHelpers.CreateCorrelationId();
            var pipeline = CreatePipeline(
                record,
                address,
                options,
                catalog,
                pageAssetCache,
                pageAssetFetcher,
                loggerFactory,
                correlationId);
            var results = await pipeline.SearchAsync(query ?? string.Empty, cancellationToken);

            return Results.Ok(results.Select(result => new
            {
                Id = result.Id.ToString(),
                Source = result.SourceId,
                result.Title,
                MediaType = result.MediaType.ToString().ToLowerInvariant()
            }));
        });

        app.MapGet("/pipeline/paged/chapters", async (
            string? mediaId,
            string? pluginId,
            PluginResolutionService pluginResolution,
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
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            var correlationId = PluginGrpcHelpers.CreateCorrelationId();
            var pipeline = CreatePipeline(
                record,
                address,
                options,
                catalog,
                pageAssetCache,
                pageAssetFetcher,
                loggerFactory,
                correlationId);
            var chapters = await pipeline.GetChaptersAsync(MediaId.Create(mediaId), cancellationToken);

            return Results.Ok(chapters.Select(chapter => new
            {
                Id = chapter.ChapterId,
                chapter.Number,
                chapter.Title
            }));
        });

        app.MapGet("/pipeline/paged/page", async (
            string? mediaId,
            string? chapterId,
            int? index,
            string? pluginId,
            PluginResolutionService pluginResolution,
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
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            var correlationId = PluginGrpcHelpers.CreateCorrelationId();
            var pipeline = CreatePipeline(
                record,
                address,
                options,
                catalog,
                pageAssetCache,
                pageAssetFetcher,
                loggerFactory,
                correlationId);
            var page = await pipeline.GetPageAsync(
                MediaId.Create(mediaId),
                chapterId,
                index ?? 0,
                cancellationToken);

            return Results.Ok(new
            {
                Id = page.PageId,
                page.Index,
                ContentUri = page.ContentUri.ToString()
            });
        });

        app.MapGet("/pipeline/paged/page-asset", async (
            string? mediaId,
            string? chapterId,
            int? index,
            string? pluginId,
            PluginResolutionService pluginResolution,
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
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            using var usageLease = processManager.AcquireUsageLease(record.Manifest.Id);

            var correlationId = PluginGrpcHelpers.CreateCorrelationId();
            var pipeline = CreatePipeline(
                record,
                address,
                options,
                catalog,
                pageAssetCache,
                pageAssetFetcher,
                loggerFactory,
                correlationId);
            var page = await pipeline.GetPageAsync(
                MediaId.Create(mediaId),
                chapterId,
                index ?? 0,
                cancellationToken);

            var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, options.Value.ProbeTimeoutSeconds));
            var logger = loggerFactory.CreateLogger("PagedPipelineEndpoints");
            logger.LogInformation(
                "Page asset fetch {CorrelationId} pluginId={PluginId} deadline={DeadlineUtc}",
                correlationId,
                record.Manifest.Id,
                deadlineUtc.ToString("O"));

            var asset = await pipeline.GetPageAssetAsync(page, cancellationToken);
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
