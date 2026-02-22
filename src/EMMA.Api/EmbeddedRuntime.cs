using EMMA.Application.Pipelines;
using EMMA.Application.Ports;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.Http;
using EMMA.Infrastructure.InMemory;

namespace EMMA.Api;

/// <summary>
/// Embedded runtime composition for in-process usage.
/// </summary>
public sealed record EmbeddedRuntime(
    PagedMediaPipeline Pipeline,
    ICachePort MetadataCache,
    IPageAssetCachePort PageAssetCache,
    IPageAssetFetcherPort PageAssetFetcher);

public static class EmbeddedRuntimeFactory
{
    public static EmbeddedRuntime Create(
        IMediaSearchPort search,
        IPageProviderPort pages,
        IPolicyEvaluator policy,
        EmbeddedRuntimeOptions? options = null,
        ICachePort? metadataCache = null,
        IPageAssetCachePort? pageAssetCache = null,
        IPageAssetFetcherPort? pageAssetFetcher = null)
    {
        var resolvedOptions = options ?? EmbeddedRuntimeOptions.Default;
        var cache = metadataCache ?? new InMemoryCachePort();
        var assetCache = pageAssetCache ?? new BoundedPageAssetCache(resolvedOptions.PageAssetCacheOptions);
        var assetFetcher = pageAssetFetcher ?? new HttpPageAssetFetcher();

        var pipeline = new PagedMediaPipeline(
            search,
            pages,
            policy,
            cache,
            resolvedOptions.PipelineOptions,
            assetCache,
            assetFetcher);

        return new EmbeddedRuntime(pipeline, cache, assetCache, assetFetcher);
    }
}
