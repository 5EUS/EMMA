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
            PluginRegistry registry,
            IOptions<PluginHostOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var (record, address, error) = TryResolvePlugin(registry, pluginId);
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            var pipeline = CreatePipeline(record, address, options, loggerFactory);
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
            PluginRegistry registry,
            IOptions<PluginHostOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                return Results.BadRequest(new { message = "mediaId is required." });
            }

            var (record, address, error) = TryResolvePlugin(registry, pluginId);
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            var pipeline = CreatePipeline(record, address, options, loggerFactory);
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
            PluginRegistry registry,
            IOptions<PluginHostOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                return Results.BadRequest(new { message = "mediaId and chapterId are required." });
            }

            var (record, address, error) = TryResolvePlugin(registry, pluginId);
            if (error is not null || record is null || address is null)
            {
                return error ?? Results.Problem("Plugin resolution failed.");
            }

            var pipeline = CreatePipeline(record, address, options, loggerFactory);
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

        return app;
    }

    private static PagedMediaPipeline CreatePipeline(
        PluginRecord record,
        Uri address,
        IOptions<PluginHostOptions> options,
        ILoggerFactory loggerFactory)
    {
        var correlationId = PluginGrpcHelpers.CreateCorrelationId();
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
            pipelineOptions);
    }

    private static (PluginRecord? Record, Uri? Address, IResult? Error) TryResolvePlugin(
        PluginRegistry registry,
        string? pluginId)
    {
        var snapshot = registry.GetSnapshot();
        if (snapshot.Count == 0)
        {
            return (null, null, Results.NotFound(new { message = "No matching plugin record found." }));
        }

        PluginRecord? record = null;
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            record = snapshot[0];
        }
        else
        {
            record = snapshot.FirstOrDefault(item =>
                string.Equals(item.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        if (record is null)
        {
            return (null, null, Results.NotFound(new { message = "No matching plugin record found." }));
        }

        if (record.Manifest.Entry is null)
        {
            return (record, null, Results.Problem("Plugin manifest has no entry."));
        }

        if (!string.Equals(record.Manifest.Entry.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            return (record, null, Results.Problem($"Unsupported plugin protocol: {record.Manifest.Entry.Protocol}."));
        }

        if (string.IsNullOrWhiteSpace(record.Manifest.Entry.Endpoint))
        {
            return (record, null, Results.Problem("Plugin manifest entry is missing endpoint."));
        }

        if (!Uri.TryCreate(record.Manifest.Entry.Endpoint, UriKind.Absolute, out var address))
        {
            return (record, null, Results.Problem("Plugin manifest entry endpoint is invalid."));
        }

        return (record, address, null);
    }
}
