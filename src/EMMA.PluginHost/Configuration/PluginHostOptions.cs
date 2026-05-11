namespace EMMA.PluginHost.Configuration;

/// <summary>
/// Controls how the plugin host resolves the native helper library used for in-process WASM execution.
/// </summary>
public enum NativeWasmLibraryMode
{
    /// <summary>
    /// Lets the host choose the appropriate native library strategy for the current platform.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Forces the host to use the internally managed native WASM helper library.
    /// </summary>
    Internal,

    /// <summary>
    /// Forces the host to use an externally supplied native WASM helper library.
    /// </summary>
    External
}

/// <summary>
/// Configuration settings for plugin host startup and handshake behavior.
/// </summary>
public sealed class PluginHostOptions
{
    /// <summary>
    /// Gets the relative or absolute directory that contains installed plugin manifests.
    /// </summary>
    public string ManifestDirectory { get; init; } = "manifests";

    /// <summary>
    /// Gets the directory used to persist repository metadata and cached catalog state.
    /// </summary>
    public string RepositoryDirectory { get; init; } = "repositories";

    /// <summary>
    /// Gets the maximum number of seconds allowed for an initial plugin handshake.
    /// </summary>
    public int HandshakeTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Gets a value indicating whether plugins should be handshaken proactively during startup.
    /// </summary>
    public bool HandshakeOnStartup { get; init; } = true;

    /// <summary>
    /// Gets the root directory used to create per-plugin sandbox working areas.
    /// </summary>
    public string SandboxRootDirectory { get; init; } = "sandbox";

    /// <summary>
    /// Gets a value indicating whether sandbox preparation and enforcement are enabled.
    /// </summary>
    public bool SandboxEnabled { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether plugin startup may continue when sandboxing is unavailable.
    /// </summary>
    public bool AllowNoSandboxFallback { get; init; } = false;

    /// <summary>
    /// Gets an override that enables or disables process-based plugins for the current host.
    /// </summary>
    public bool? EnableProcessPlugins { get; init; } = null;

    /// <summary>
    /// Gets an override that enables or disables WASM plugins for the current host.
    /// </summary>
    public bool? EnableWasmPlugins { get; init; } = null;

    /// <summary>
    /// Gets an override that enables or disables externally hosted endpoint plugins.
    /// </summary>
    public bool? EnableExternalEndpointPlugins { get; init; } = null;

    /// <summary>
    /// Gets the native helper library selection mode for in-process WASM execution.
    /// </summary>
    public NativeWasmLibraryMode NativeWasmLibraryMode { get; init; } = NativeWasmLibraryMode.Auto;

    /// <summary>
    /// Gets the number of seconds between budget monitoring passes.
    /// </summary>
    public int BudgetWatchIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Gets the CPU budget threshold, in milliseconds, used when monitoring plugin activity.
    /// </summary>
    public int MaxCpuBudgetMs { get; init; } = 250;

    /// <summary>
    /// Gets the memory budget threshold, in megabytes, used for plugin monitoring.
    /// </summary>
    public int MaxMemoryMb { get; init; } = 512;

    /// <summary>
    /// Gets the maximum number of seconds allowed for plugin startup.
    /// </summary>
    public int StartupTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Gets the delay, in milliseconds, between startup readiness probes.
    /// </summary>
    public int StartupProbeIntervalMs { get; init; } = 200;

    /// <summary>
    /// Gets the number of seconds to add between retry attempts after a timeout.
    /// </summary>
    public int TimeoutBackoffSeconds { get; init; } = 5;

    /// <summary>
    /// Gets the maximum number of retry attempts allowed after timeout failures.
    /// </summary>
    public int MaxTimeoutRetries { get; init; } = 3;

    /// <summary>
    /// Gets the number of seconds allowed for an individual health probe.
    /// </summary>
    public int ProbeTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Gets the per-plugin cap on concurrent calls issued by the host.
    /// </summary>
    public int MaxConcurrentCallsPerPlugin { get; init; } = 8;

    /// <summary>
    /// Gets the maximum number of log lines retained per plugin.
    /// </summary>
    public int PluginLogMaxLines { get; init; } = 200;

    /// <summary>
    /// Gets the number of idle seconds after which a plugin process may be stopped.
    /// </summary>
    public int PluginIdleTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Gets the number of seconds between idle process cleanup sweeps.
    /// </summary>
    public int PluginIdleSweepSeconds { get; init; } = 30;

    /// <summary>
    /// Gets the default timeout, in seconds, for individual WASM operations.
    /// </summary>
    public int WasmOperationTimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Gets the HTTP timeout, in seconds, used for repository catalog and artifact requests.
    /// </summary>
    public int RepositoryRequestTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Gets the maximum allowed repository catalog size in bytes.
    /// </summary>
    public int RepositoryMaxCatalogBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum allowed repository artifact size in bytes.
    /// </summary>
    public int RepositoryMaxArtifactBytes { get; init; } = 512 * 1024 * 1024;

    /// <summary>
    /// Gets a value indicating whether plain HTTP repository endpoints are allowed.
    /// </summary>
    public bool AllowInsecureRepositoryHttp { get; init; } = false;

    /// <summary>
    /// Gets the HTTP user agent string sent for repository requests.
    /// </summary>
    public string RepositoryHttpUserAgent { get; init; } = "EMMA.PluginHost/1.0";
}
