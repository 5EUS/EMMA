namespace EMMA.Api.Configuration;

/// <summary>How many requests the API admits.</summary>
/// <param name="PerClientPermits">Requests one authenticated client may make per window.</param>
/// <param name="PerClientWindow">The length of the window.</param>
/// <param name="GlobalConcurrency">Requests the whole API serves at once, across clients.</param>
/// <param name="GlobalQueue">Requests allowed to wait for a free slot before being refused.</param>
internal sealed record RateLimitSettings(int PerClientPermits, TimeSpan PerClientWindow, int GlobalConcurrency, int GlobalQueue)
{
    /// <summary>Gets the limits used when nothing is configured.</summary>
    public static RateLimitSettings Default { get; } = new(60, TimeSpan.FromSeconds(60), 64, 0);
}
