using EMMA.Application.Pipelines;
using EMMA.Infrastructure.Cache;

namespace EMMA.Api;

/// <summary>
/// Options for embedded runtime composition.
/// </summary>
public sealed record EmbeddedRuntimeOptions(
    PagedMediaPipelineOptions PipelineOptions,
    PageAssetCacheOptions PageAssetCacheOptions)
{
    public static EmbeddedRuntimeOptions Default { get; } = new(
        PagedMediaPipelineOptions.Default,
        PageAssetCacheOptions.Default);
}
