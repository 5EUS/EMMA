namespace EMMA.Application.Pipelines;

/// <summary>Limits for turning assets into bytes.</summary>
/// <param name="MaxPayloadBytes">The most bytes accepted for one asset, however it is delivered.</param>
public sealed record AssetPipelineOptions(long MaxPayloadBytes)
{
    /// <summary>Gets the defaults: 64 MiB per asset.</summary>
    public static AssetPipelineOptions Default { get; } = new(64L * 1024 * 1024);
}
