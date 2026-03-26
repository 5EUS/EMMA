namespace EMMA.Application.Pipelines;

/// <summary>
/// Options for paged media pipeline timeouts and retries.
/// </summary>
public sealed record PagedMediaPipelineOptions(
    TimeSpan PageTimeout,
    int PageRetryCount,
    TimeSpan PageRetryDelay)
{
    public static PagedMediaPipelineOptions Default { get; } = new(
        TimeSpan.FromSeconds(5),
        1,
        TimeSpan.FromMilliseconds(200));
}
