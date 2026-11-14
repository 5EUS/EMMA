namespace EMMA.Application.Streaming;

/// <summary>Limits for <see cref="StreamProxy"/>.</summary>
public sealed record StreamProxyOptions
{
    /// <summary>Gets the defaults.</summary>
    public static StreamProxyOptions Default { get; } = new();

    /// <summary>Gets how many sessions are kept; the one unused longest goes first.</summary>
    public int MaxSessions { get; init; } = 32;

    /// <summary>Gets how long an unused session is kept.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Gets how many addresses one session may register.</summary>
    public int MaxPartsPerSession { get; init; } = 50_000;

    /// <summary>Gets the largest playlist or manifest that is read and rewritten, in bytes.</summary>
    public long MaxManifestBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Gets the largest segment that is read whole and cached, in bytes. A larger one is passed through.</summary>
    public long MaxSegmentBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Gets the size of the pieces a plugin-served asset is read in, in bytes.</summary>
    public int ProxiedChunkBytes { get; init; } = 1024 * 1024;
}
