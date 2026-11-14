namespace EMMA.Application.Pipelines;

/// <summary>Limits for turning assets into bytes.</summary>
/// <param name="MaxPayloadBytes">The most bytes accepted for one asset, however it is delivered.</param>
public sealed record AssetPipelineOptions(long MaxPayloadBytes)
{
    /// <summary>Gets the defaults: 64 MiB per asset, resolutions kept for five minutes.</summary>
    public static AssetPipelineOptions Default { get; } = new(64L * 1024 * 1024);

    /// <summary>
    /// Gets how long a resolution is reused before the plugin is asked again. A link that says it expires sooner wins.
    /// <see cref="TimeSpan.Zero"/> turns resolution caching off.
    /// </summary>
    public TimeSpan ResolutionTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets how long before a link's stated expiry a cached resolution stops being served, so a client never starts a fetch with a link about to die.</summary>
    public TimeSpan ExpiryMargin { get; init; } = TimeSpan.FromSeconds(30);
}
