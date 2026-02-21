namespace EMMA.PluginHost.Configuration;

/// <summary>
/// Configuration settings for plugin host startup and handshake behavior.
/// </summary>
public sealed class PluginHostOptions
{
    public string ManifestDirectory { get; init; } = "plugins";
    public int HandshakeTimeoutSeconds { get; init; } = 5;
    public bool HandshakeOnStartup { get; init; } = true;
    public string SandboxRootDirectory { get; init; } = "sandbox";
    public bool SandboxEnabled { get; init; } = false; // TODO default to true once sandboxing is implemented and stable
    public int BudgetWatchIntervalSeconds { get; init; } = 30;
    public int MaxCpuBudgetMs { get; init; } = 0;
    public int MaxMemoryMb { get; init; } = 0;
    public int StartupTimeoutSeconds { get; init; } = 5;
    public int StartupProbeIntervalMs { get; init; } = 200;
    public int TimeoutBackoffSeconds { get; init; } = 5;
    public int MaxTimeoutRetries { get; init; } = 3;
}
