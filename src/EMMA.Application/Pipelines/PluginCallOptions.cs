namespace EMMA.Application.Pipelines;

/// <summary>Retry and timeout behaviour for calls into a plugin.</summary>
/// <param name="MaxAttempts">Total attempts including the first; at least 1.</param>
/// <param name="BaseDelay">Delay before the first retry; doubles each attempt.</param>
/// <param name="MaxDelay">Upper bound on any delay, including a delay the plugin asked for.</param>
/// <param name="AttemptTimeout">How long one attempt may run before it is abandoned.</param>
public sealed record PluginCallOptions(int MaxAttempts, TimeSpan BaseDelay, TimeSpan MaxDelay, TimeSpan AttemptTimeout)
{
    /// <summary>Gets the defaults: 3 attempts, 250 ms doubling up to 5 s, 30 s per attempt.</summary>
    public static PluginCallOptions Default { get; } =
        new(3, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
}
