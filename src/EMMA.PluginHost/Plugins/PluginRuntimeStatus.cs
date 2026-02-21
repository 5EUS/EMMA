namespace EMMA.PluginHost.Plugins;

/// <summary>
/// Tracks plugin runtime lifecycle status.
/// </summary>
public sealed record PluginRuntimeStatus(
    PluginRuntimeState State,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset Timestamp,
    int RetryCount,
    DateTimeOffset? NextRetryAt,
    int? ExitCode)
{
    public static PluginRuntimeStatus Unknown() => new(
        PluginRuntimeState.Unknown,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        null,
        null);

    public static PluginRuntimeStatus External() => new(
        PluginRuntimeState.External,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        null,
        null);

    public static PluginRuntimeStatus Running() => new(
        PluginRuntimeState.Running,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        null,
        null);

    public static PluginRuntimeStatus Starting() => new(
        PluginRuntimeState.Starting,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        null,
        null);

    public static PluginRuntimeStatus Stopped() => new(
        PluginRuntimeState.Stopped,
        null,
        null,
        DateTimeOffset.UtcNow,
        0,
        null,
        null);

    public static PluginRuntimeStatus Failed(string code, string message, int? exitCode = null) => new(
        PluginRuntimeState.Crashed,
        code,
        message,
        DateTimeOffset.UtcNow,
        0,
        null,
        exitCode);

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
            NextRetryAt = nextRetryAt
        };

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
