namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Tracks plugin runtime lifecycle status.
/// </summary>
/// <param name="State">The current runtime state.</param>
/// <param name="LastErrorCode">The last runtime error code.</param>
/// <param name="LastErrorMessage">The last runtime error message.</param>
/// <param name="Timestamp">The UTC timestamp when the status was recorded.</param>
/// <param name="RetryCount">The number of restart or timeout retries attempted.</param>
/// <param name="TimeoutCount">The number of timeout events recorded.</param>
/// <param name="NextRetryAt">The next scheduled retry time, if any.</param>
/// <param name="ExitCode">The last process exit code, if known.</param>
public sealed record PluginRuntimeStatus(
    PluginRuntimeState State,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset Timestamp,
    int RetryCount,
    int TimeoutCount,
    DateTimeOffset? NextRetryAt,
    int? ExitCode)
{
    /// <summary>
    /// Creates a runtime status representing an unknown state.
    /// </summary>
    /// <returns>A runtime status in the <see cref="PluginRuntimeState.Unknown"/> state.</returns>
    public static PluginRuntimeStatus Unknown() => new(
        PluginRuntimeState.Unknown,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        0,
        null,
        null);

    /// <summary>
    /// Creates a runtime status representing an externally managed plugin.
    /// </summary>
    /// <returns>A runtime status in the <see cref="PluginRuntimeState.External"/> state.</returns>
    public static PluginRuntimeStatus External() => new(
        PluginRuntimeState.External,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        0,
        null,
        null);

    /// <summary>
    /// Creates a runtime status representing a running plugin.
    /// </summary>
    /// <returns>A runtime status in the <see cref="PluginRuntimeState.Running"/> state.</returns>
    public static PluginRuntimeStatus Running() => new(
        PluginRuntimeState.Running,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        0,
        null,
        null);

    /// <summary>
    /// Creates a runtime status representing a plugin that is starting.
    /// </summary>
    /// <returns>A runtime status in the <see cref="PluginRuntimeState.Starting"/> state.</returns>
    public static PluginRuntimeStatus Starting() => new(
        PluginRuntimeState.Starting,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        0,
        null,
        null);

    /// <summary>
    /// Creates a runtime status representing a stopped plugin.
    /// </summary>
    /// <returns>A runtime status in the <see cref="PluginRuntimeState.Stopped"/> state.</returns>
    public static PluginRuntimeStatus Stopped() => new(
        PluginRuntimeState.Stopped,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        0,
        null,
        null);

    /// <summary>
    /// Creates a runtime status representing a failed plugin execution.
    /// </summary>
    /// <param name="code">The failure code.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="exitCode">The optional process exit code.</param>
    /// <returns>A runtime status in the <see cref="PluginRuntimeState.Crashed"/> state.</returns>
    public static PluginRuntimeStatus Failed(string code, string message, int? exitCode = null) => new(
        PluginRuntimeState.Crashed,
        code,
        message,
        DateTimeOffset.UtcNow,
        0,
        0,
        null,
        exitCode);

    /// <summary>
    /// Returns a copy of the status with a new runtime state and optional error details.
    /// </summary>
    /// <param name="state">The new runtime state.</param>
    /// <param name="code">The error code to store.</param>
    /// <param name="message">The error message to store.</param>
    /// <param name="exitCode">The optional process exit code.</param>
    /// <returns>An updated runtime status.</returns>
    public PluginRuntimeStatus WithState(
        PluginRuntimeState state,
        string? code,
        string? message,
        int? exitCode = null) =>
        this with
        {
            State = state,
            LastErrorCode = code,
            LastErrorMessage = message,
            Timestamp = DateTimeOffset.UtcNow,
            ExitCode = exitCode
        };

    /// <summary>
    /// Returns a copy of the status updated for a timeout retry.
    /// </summary>
    /// <param name="retryCount">The retry attempt count.</param>
    /// <param name="nextRetryAt">The next retry time.</param>
    /// <param name="code">The timeout code.</param>
    /// <param name="message">The timeout message.</param>
    /// <returns>An updated runtime status.</returns>
    public PluginRuntimeStatus WithRetry(
        int retryCount,
        DateTimeOffset? nextRetryAt,
        string code,
        string message) =>
        this with
        {
            State = PluginRuntimeState.Timeout,
            LastErrorCode = code,
            LastErrorMessage = message,
            Timestamp = DateTimeOffset.UtcNow,
            RetryCount = retryCount,
            TimeoutCount = TimeoutCount + 1,
            NextRetryAt = nextRetryAt
        };

    /// <summary>
    /// Returns a copy of the status updated to the quarantined state.
    /// </summary>
    /// <param name="code">The quarantine reason code.</param>
    /// <param name="message">The quarantine message.</param>
    /// <returns>An updated runtime status.</returns>
    public PluginRuntimeStatus Quarantined(string code, string message) =>
        this with
        {
            State = PluginRuntimeState.Quarantined,
            LastErrorCode = code,
            LastErrorMessage = message,
            Timestamp = DateTimeOffset.UtcNow,
            NextRetryAt = null
        };
}
