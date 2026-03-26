namespace EMMA.PluginHost.Configuration;

public enum NativeWasmLibraryMode
{
    Auto = 0,
    Internal,
    External
}

/// <summary>
/// Configuration settings for plugin host startup and handshake behavior.
/// </summary>
public sealed class PluginHostOptions
{
    public string ManifestDirectory { get; init; } = "manifests";
    public string RepositoryDirectory { get; init; } = "repositories";
    public int HandshakeTimeoutSeconds { get; init; } = 5;
    public bool HandshakeOnStartup { get; init; } = true;
    public string SandboxRootDirectory { get; init; } = "sandbox";
    public bool SandboxEnabled { get; init; } = true;
    public bool AllowNoSandboxFallback { get; init; } = false;
    public bool? EnableProcessPlugins { get; init; } = null;
    public bool? EnableWasmPlugins { get; init; } = null;
    public bool? EnableExternalEndpointPlugins { get; init; } = null;
    public NativeWasmLibraryMode NativeWasmLibraryMode { get; init; } = NativeWasmLibraryMode.Auto;
    public int BudgetWatchIntervalSeconds { get; init; } = 30;
    public int MaxCpuBudgetMs { get; init; } = 250;
    public int MaxMemoryMb { get; init; } = 512;
    public int StartupTimeoutSeconds { get; init; } = 5;
    public int StartupProbeIntervalMs { get; init; } = 200;
    public int TimeoutBackoffSeconds { get; init; } = 5;
    public int MaxTimeoutRetries { get; init; } = 3;
    public int ProbeTimeoutSeconds { get; init; } = 5;
    public int MaxConcurrentCallsPerPlugin { get; init; } = 8;
    public int PluginLogMaxLines { get; init; } = 200;
    public int PluginIdleTimeoutSeconds { get; init; } = 120;
    public int PluginIdleSweepSeconds { get; init; } = 30;
    public int WasmOperationTimeoutSeconds { get; init; } = 15;
    public int RepositoryRequestTimeoutSeconds { get; init; } = 30;
    public int RepositoryMaxCatalogBytes { get; init; } = 2 * 1024 * 1024;
    public int RepositoryMaxArtifactBytes { get; init; } = 512 * 1024 * 1024;
    public bool AllowInsecureRepositoryHttp { get; init; } = false;
    public string RepositoryHttpUserAgent { get; init; } = "EMMA.PluginHost/1.0";
}
