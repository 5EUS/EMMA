using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EMMA.Application.Ports;
using PluginContracts = EMMA.Contracts.Plugins;
using EMMA.Infrastructure.Cache;
using EMMA.Infrastructure.Http;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Plugins;
using EMMA.PluginHost.Sandboxing;
using EMMA.PluginHost.Services;
using EMMA.Storage;
using Grpc.Core;
using Grpc.Net.Client;
using EMMA.Domain;

namespace EMMA.PluginHost.Library;

/// <summary>
/// Native FFI exports for embedding the PluginHost in-process.
/// This provides both a managed C# API and a thin FFI marshalling layer.
/// </summary>
public static class PluginHostExports
{
    private const string DefaultProgressUserId = "local";
    private const string NativeWasmLibraryModeEnvVar = "EMMA_NATIVE_WASM_LIBRARY_MODE";
    private const string EmbeddedHandshakeOnStartupEnvVar = "EMMA_PLUGINHOST_HANDSHAKE_ON_STARTUP";
    private const string PluginHostHandshakeOnStartupEnvVar = "PluginHost__HandshakeOnStartup";
    private const string RequireSignedPluginsEnvVar = "EMMA_REQUIRE_SIGNED_PLUGINS";
    private const string PluginSignatureRequireSignedEnvVar = "PluginSignature__RequireSignedPlugins";
    private const string PluginSignatureHmacKeyEnvVar = "EMMA_PLUGIN_SIGNATURE_HMAC_KEY_BASE64";
    private const string PluginSignatureHmacKeyConfigEnvVar = "PluginSignature__HmacKeyBase64";
    private const string DevModeEnvVar = "EMMA_PLUGIN_DEV_MODE";
    private const string PluginHostConsoleLogsEnvVar = "EMMA_PLUGINHOST_CONSOLE_LOGS";
    private const string PluginHostLogLevelEnvVar = "EMMA_PLUGINHOST_LOG_LEVEL";
    private const string HostAuthHeader = "x-emma-plugin-host-auth";
    private static ServiceProvider? _serviceProvider;
    private static PluginRegistry? _registry;
    private static PluginHandshakeService? _handshake;
    private static PluginResolutionService? _pluginResolution;
    private static IWasmPluginRuntimeHost? _wasmRuntime;
    private static DownloadOrchestrator? _downloadOrchestrator;
    private static bool _initialized = false;
    private static readonly Lock _initLock = new();
    private static readonly Lock _errorLock = new();
    private static readonly ConcurrentDictionary<string, GrpcChannel> _grpcChannelCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, MediaPage> _pageCache = new(StringComparer.Ordinal);
    private static readonly Lock _searchTimingLock = new();

    // Don't use [ThreadStatic] - we need the error to be visible across threads for FFI
    private static string? _lastError;
    private static string? _lastSearchTiming;

    // ==================== Managed API (callable from C#) ====================

    /// <summary>
    /// Initialize the plugin host with the specified directories.
    /// Returns 0 on success, non-zero on failure.
    /// </summary>
    public static int InitializeManaged(string manifestsDir, string sandboxDir)
    {
        ClearLastError();

        try
        {
            lock (_initLock)
            {
                if (_initialized)
                {
                    return 0; // Already initialized
                }

                InitializeSqliteForEmbeddedHost();

                var services = new ServiceCollection();

                // Configure options - use PostConfigure since properties are init-only
                services.AddOptions<PluginHostOptions>()
                    .PostConfigure(options =>
                    {
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.ManifestDirectory))!
                            .SetValue(options, manifestsDir);
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.SandboxRootDirectory))!
                            .SetValue(options, sandboxDir);
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.HandshakeOnStartup))!
                            .SetValue(options, ResolveHandshakeOnStartupEnabled());
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.WasmOperationTimeoutSeconds))!
                             .SetValue(options, 15);
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.NativeWasmLibraryMode))!
                            .SetValue(options, ResolveNativeWasmLibraryMode());
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.SandboxEnabled))!
                            .SetValue(options, true);
                    });

                services.AddOptions<PluginSignatureOptions>()
                    .PostConfigure(options =>
                    {
                        typeof(PluginSignatureOptions).GetProperty(nameof(PluginSignatureOptions.RequireSignedPlugins))!
                            .SetValue(options, ResolveRequireSignedPlugins());

                        var hmacKey = ResolvePluginSignatureHmacKey();
                        if (!string.IsNullOrWhiteSpace(hmacKey))
                        {
                            typeof(PluginSignatureOptions).GetProperty(nameof(PluginSignatureOptions.HmacKeyBase64))!
                                .SetValue(options, hmacKey);
                        }
                    });

                // Register core services
                services.AddSingleton<PluginRegistry>();
                services.AddSingleton<PluginManifestLoader>();
                services.AddSingleton<PluginPermissionSanitizer>();
                services.AddSingleton<IPluginEntrypointResolver, PluginEntrypointResolver>();
                services.AddSingleton<PluginResolutionService>();
                services.AddSingleton<PluginRepositoryStore>();
                services.AddSingleton<PluginRepositoryCatalogClient>();
                services.AddSingleton<PluginRepositoryService>();
                services.AddSingleton<PluginRepositoryInstallOrchestrator>();
                services.AddSingleton<PluginEndpointAllocator>();
                services.AddSingleton<PluginProcessManager>();
                services.AddSingleton<IWasmComponentInvoker, NativeInProcessWasmComponentInvoker>();
                services.AddSingleton<IWasmPluginRuntimeHost, WasmPluginRuntimeHost>();
                services.AddSingleton<IPluginSignatureVerifier, HmacPluginSignatureVerifier>();

                // Storage
                services.AddSingleton(StorageOptions.Default);
                services.AddSingleton<StorageInitializer>();
                services.AddSingleton(PageAssetCacheOptions.Default);
                services.AddSingleton<IMediaCatalogPort, SqliteMediaCatalogPort>();
                services.AddSingleton<ILibraryPort, SqliteLibraryPort>();
                services.AddSingleton<IProgressPort, SqliteProgressPort>();
                services.AddSingleton<IHistoryPort, SqliteHistoryPort>();
                services.AddSingleton<IDownloadPort, SqliteDownloadPort>();
                services.AddSingleton<IPageAssetCachePort>(sp =>
                    new BoundedPageAssetCache(sp.GetRequiredService<PageAssetCacheOptions>()));
                services.AddSingleton<IPageAssetFetcherPort, HttpPageAssetFetcher>();

                // Sandbox manager
                services.AddSingleton<IPluginSandboxManager>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<PluginHostOptions>>();

                    if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS())
                        return new IosPluginSandboxManager(options, sp.GetService<ILogger<IosPluginSandboxManager>>()!);
                    if (OperatingSystem.IsMacOS())
                        return new MacOsPluginSandboxManager(options, sp.GetService<ILogger<MacOsPluginSandboxManager>>()!);
                    if (OperatingSystem.IsLinux())
                        return new LinuxPluginSandboxManager(options, sp.GetService<ILogger<LinuxPluginSandboxManager>>()!);

                    return new WindowsPluginSandboxManager(options, sp.GetService<ILogger<WindowsPluginSandboxManager>>()!);
                });

                // Handshake
                services.AddSingleton<PluginHandshakeService>();

                // Logging is consolidated through EMMA.Native by default.
                // Console provider is opt-in for host-only diagnostics.
                services.AddLogging(builder =>
                {
                    builder.ClearProviders();

                    if (ShouldEnablePluginHostConsoleLogs())
                    {
                        builder.AddSimpleConsole(options =>
                        {
                            options.SingleLine = true;
                            options.IncludeScopes = false;
                        });
                    }

                    builder.SetMinimumLevel(ResolvePluginHostMinimumLogLevel());
                });

                _serviceProvider = services.BuildServiceProvider();

                // Initialize storage
                var storageInit = _serviceProvider.GetRequiredService<StorageInitializer>();
                storageInit.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                EnsureDefaultLibraryExistsManaged();

                // Resolve services
                _registry = _serviceProvider.GetRequiredService<PluginRegistry>();
                _handshake = _serviceProvider.GetRequiredService<PluginHandshakeService>();
                _pluginResolution = _serviceProvider.GetRequiredService<PluginResolutionService>();
                _wasmRuntime = _serviceProvider.GetRequiredService<IWasmPluginRuntimeHost>();

                var downloadPort = _serviceProvider.GetRequiredService<IDownloadPort>();
                var downloadLogger = _serviceProvider
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger<DownloadOrchestrator>();
                _downloadOrchestrator = new DownloadOrchestrator(
                    downloadPort,
                    ExecuteDownloadJobAsync,
                    downloadLogger);

                // Load plugins via handshake service
                _handshake.HandshakeAllAsync(CancellationToken.None).GetAwaiter().GetResult();

                _initialized = true;
                return 0;
            }
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return -1;
        }
    }

    private static NativeWasmLibraryMode ResolveNativeWasmLibraryMode()
    {
        var value = Environment.GetEnvironmentVariable(NativeWasmLibraryModeEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return NativeWasmLibraryMode.Auto;
        }

        if (Enum.TryParse<NativeWasmLibraryMode>(value.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return value.Trim() switch
        {
            "internal" => NativeWasmLibraryMode.Internal,
            "external" => NativeWasmLibraryMode.External,
            _ => NativeWasmLibraryMode.Auto
        };
    }

    private static bool ResolveHandshakeOnStartupEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EmbeddedHandshakeOnStartupEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginHostHandshakeOnStartupEnvVar);

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (bool.TryParse(value, out var parsedBool))
            {
                return parsedBool;
            }

            return value.Trim() switch
            {
                "1" or "yes" or "on" => true,
                "0" or "no" or "off" => false,
                _ => false
            };
        }

        return true;
    }

    private static bool ResolveRequireSignedPlugins()
    {
        var value = Environment.GetEnvironmentVariable(RequireSignedPluginsEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureRequireSignedEnvVar);

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (bool.TryParse(value, out var parsedBool))
            {
                return parsedBool;
            }

            return value.Trim() switch
            {
                "1" or "yes" or "on" => true,
                "0" or "no" or "off" => false,
                _ => true
            };
        }

        return !IsDevelopmentMode();
    }

    private static string? ResolvePluginSignatureHmacKey()
    {
        return Environment.GetEnvironmentVariable(PluginSignatureHmacKeyEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureHmacKeyConfigEnvVar);
    }

    private static bool IsDevelopmentMode()
    {
        var explicitDev = Environment.GetEnvironmentVariable(DevModeEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitDev))
        {
            if (bool.TryParse(explicitDev, out var parsedBool))
            {
                return parsedBool;
            }

            return explicitDev.Trim() switch
            {
                "1" or "yes" or "on" => true,
                _ => false
            };
        }

        var aspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(aspnetEnvironment, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldEnablePluginHostConsoleLogs()
    {
        var value = Environment.GetEnvironmentVariable(PluginHostConsoleLogsEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var parsedBool))
        {
            return parsedBool;
        }

        return value.Trim() is "1" or "yes" or "on";
    }

    private static LogLevel ResolvePluginHostMinimumLogLevel()
    {
        var value = Environment.GetEnvironmentVariable(PluginHostLogLevelEnvVar);
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<LogLevel>(value.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return LogLevel.Warning;
    }

    /// <summary>
    /// Shutdown the plugin host and release resources.
    /// </summary>
    public static void ShutdownManaged()
    {
        lock (_initLock)
        {
            if (!_initialized)
            {
                return;
            }

            _serviceProvider?.Dispose();
            _serviceProvider = null;
            _registry = null;
            _handshake = null;
            _pluginResolution = null;
            _wasmRuntime = null;
            _downloadOrchestrator?.Dispose();
            _downloadOrchestrator = null;
            foreach (var pair in _grpcChannelCache)
            {
                pair.Value.Dispose();
            }
            _grpcChannelCache.Clear();
            _pageCache.Clear();
            _initialized = false;
        }
    }

    /// <summary>
    /// List all available plugins as JSON.
    /// Returns null on error, check GetLastErrorManaged().
    /// </summary>
    public static string? ListPluginsJsonManaged()
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            var records = _registry!.GetSnapshot();
            var summaries = records.Select(r => new PluginSummaryResponse(
                Id: r.Manifest.Id,
                Title: r.Manifest.Name ?? r.Manifest.Id,
                Version: r.Manifest.Version ?? "1.0.0",
                Author: r.Manifest.Author ?? "Unknown",
                SearchExperience: r.Manifest.SearchExperience
            )).ToList();

            return JsonSerializer.Serialize(summaries, PluginHostExportsJsonContext.Default.ListPluginSummaryResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    /// <summary>
    /// Reload plugin manifests and refresh runtime/handshake state without recreating the host.
    /// Returns 0 on success, non-zero on failure.
    /// </summary>
    public static int RescanManaged()
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            _handshake!.RescanAsync(CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return -1;
        }
    }

    public static string? ListPluginRepositoriesJsonManaged()
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var repositoryService = _serviceProvider!.GetRequiredService<PluginRepositoryService>();
            var repositories = repositoryService.ListRepositoriesAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return JsonSerializer.Serialize(
                repositories,
                PluginHostExportsJsonContext.Default.IReadOnlyListPluginRepositoryRecord);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static int AddPluginRepositoryManaged(
        string catalogUrl,
        string? repositoryId,
        string? name,
        string? sourceRepositoryUrl)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var repositoryService = _serviceProvider!.GetRequiredService<PluginRepositoryService>();
            repositoryService.AddRepositoryAsync(
                    new AddPluginRepositoryRequest(
                        CatalogUrl: catalogUrl,
                        RepositoryId: repositoryId,
                        Name: name,
                        SourceRepositoryUrl: sourceRepositoryUrl),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static int RemovePluginRepositoryManaged(string repositoryId)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var repositoryService = _serviceProvider!.GetRequiredService<PluginRepositoryService>();
            var removed = repositoryService.RemoveRepositoryAsync(repositoryId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (!removed)
            {
                SetLastError($"Repository '{repositoryId}' was not found.");
                return 0;
            }

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static string? ListRepositoryPluginsJsonManaged(string repositoryId, bool refreshCatalog)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var repositoryService = _serviceProvider!.GetRequiredService<PluginRepositoryService>();
            var result = repositoryService.GetRepositoryPluginsAsync(repositoryId, refreshCatalog, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return JsonSerializer.Serialize(result, PluginHostExportsJsonContext.Default.RepositoryPluginsResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? ListAllRepositoryPluginsJsonManaged(bool refreshCatalog)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var repositoryService = _serviceProvider!.GetRequiredService<PluginRepositoryService>();
            var plugins = repositoryService.GetAllRepositoryPluginsAsync(refreshCatalog, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return JsonSerializer.Serialize(
                plugins,
                PluginHostExportsJsonContext.Default.IReadOnlyListPluginRepositoryPluginView);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? InstallFromRepositoryJsonManaged(
        string repositoryId,
        string pluginId,
        string? version,
        bool refreshCatalog,
        bool rescanAfterInstall)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var orchestrator = _serviceProvider!.GetRequiredService<PluginRepositoryInstallOrchestrator>();
            var result = orchestrator.InstallFromRepositoryAsync(
                    new InstallPluginFromRepositoryRequest(
                        RepositoryId: repositoryId,
                        PluginId: pluginId,
                        Version: version,
                        RefreshCatalog: refreshCatalog,
                        RescanAfterInstall: rescanAfterInstall),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return JsonSerializer.Serialize(result, PluginHostExportsJsonContext.Default.PluginRepositoryInstallResult);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? EnqueueDownloadJsonManaged(
        string pluginId,
        string mediaId,
        string mediaType,
        string? chapterId,
        string? streamId)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var orchestrator = _downloadOrchestrator
                ?? throw new InvalidOperationException("Download orchestrator is not initialized.");

            var request = new DownloadEnqueueRequest(
                pluginId,
                mediaId,
                mediaType,
                chapterId,
                streamId);

            var created = orchestrator.EnqueueAsync(request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return JsonSerializer.Serialize(
                MapDownloadJobResponse(created),
                PluginHostExportsJsonContext.Default.DownloadJobResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? ListDownloadsJsonManaged(int limit = 200)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var orchestrator = _downloadOrchestrator
                ?? throw new InvalidOperationException("Download orchestrator is not initialized.");

            var jobs = orchestrator.ListAsync(Math.Max(1, limit), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var payload = jobs.Select(MapDownloadJobResponse).ToList();
            return JsonSerializer.Serialize(
                payload,
                PluginHostExportsJsonContext.Default.ListDownloadJobResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? GetDownloadJsonManaged(string jobId)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var orchestrator = _downloadOrchestrator
                ?? throw new InvalidOperationException("Download orchestrator is not initialized.");

            var job = orchestrator.GetAsync(jobId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (job is null)
            {
                SetLastError($"Download job '{jobId}' was not found.");
                return null;
            }

            return JsonSerializer.Serialize(
                MapDownloadJobResponse(job),
                PluginHostExportsJsonContext.Default.DownloadJobResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static int PauseDownloadManaged(string jobId)
    {
        return ChangeDownloadStateManaged(jobId, static (orchestrator, id) => orchestrator.PauseAsync(id, CancellationToken.None));
    }

    public static int ResumeDownloadManaged(string jobId)
    {
        return ChangeDownloadStateManaged(jobId, static (orchestrator, id) => orchestrator.ResumeAsync(id, CancellationToken.None));
    }

    public static int CancelDownloadManaged(string jobId)
    {
        return ChangeDownloadStateManaged(jobId, static (orchestrator, id) => orchestrator.CancelAsync(id, CancellationToken.None));
    }

    public static int DeleteDownloadManaged(string jobId)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var orchestrator = _downloadOrchestrator
                ?? throw new InvalidOperationException("Download orchestrator is not initialized.");

            var job = orchestrator.GetAsync(jobId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (job is null)
            {
                SetLastError($"Download job '{jobId}' was not found.");
                return 0;
            }

            var ok = orchestrator.DeleteAsync(jobId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!ok)
            {
                SetLastError($"Failed to delete download job '{jobId}'.");
                return 0;
            }

            DeleteDownloadedArtifactsForJob(job);
            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    /// <summary>
    /// Search for media using the specified plugin.
    /// Returns JSON array of MediaSummary, or null on error.
    /// </summary>
    public static string? SearchJsonManaged(string pluginId, string query)
    {
        return SearchJsonManaged(pluginId, query, null);
    }

    public static string? SearchJsonManaged(string pluginId, string query, string? correlationId)
    {
        ClearLastError();
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var ensureInitStopwatch = System.Diagnostics.Stopwatch.StartNew();
            EnsureInitialized();
            ensureInitStopwatch.Stop();

            if (!TryResolvePlugin(pluginId, out var record, out var address))
            {
                return null;
            }

            var normalizedQuery = query ?? string.Empty;

            var runtimeSearchStopwatch = System.Diagnostics.Stopwatch.StartNew();
            string json;
            if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
            {
                json = _wasmRuntime.SearchJsonAsync(record, normalizedQuery, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
                {
                    SetLastError($"Unsupported plugin protocol: {record.Manifest.Protocol}");
                    return null;
                }

                if (address is null)
                {
                    SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
                    return null;
                }

                var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
                    ? Guid.NewGuid().ToString("n")
                    : correlationId;
                var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                var channel = GetOrCreateChannel(address);
                var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
                var headers = BuildGrpcHeaders(record.Manifest.Id, resolvedCorrelationId!);
                var response = client.SearchAsync(new PluginContracts.SearchRequest
                {
                    Query = normalizedQuery,
                    Context = new PluginContracts.RequestContext
                    {
                        CorrelationId = resolvedCorrelationId,
                        DeadlineUtc = deadlineUtc.ToString("O")
                    }
                }, headers: headers, cancellationToken: CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                var results = response.Results
                    .Select(MapPluginSearchSummary)
                    .ToList();
                json = JsonSerializer.Serialize(results, PluginHostExportsJsonContext.Default.IReadOnlyListMediaSummary);
            }

            runtimeSearchStopwatch.Stop();
            return json;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
        finally
        {
            totalStopwatch.Stop();
        }
    }

    public static string? BenchmarkJsonManaged(string pluginId, int iterations)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(pluginId))
            {
                SetLastError("Plugin ID is required");
                return null;
            }

            var snapshotRecord = _registry!
                .GetSnapshot()
                .FirstOrDefault(r => string.Equals(r.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (snapshotRecord is null)
            {
                SetLastError($"Plugin '{pluginId}' not found");
                return null;
            }

            if (!_wasmRuntime!.IsWasmPlugin(snapshotRecord.Manifest))
            {
                SetLastError($"Plugin '{pluginId}' is not a WASM plugin.");
                return null;
            }

            var normalizedIterations = Math.Clamp(iterations, 1, 1000);
            return _wasmRuntime.BenchmarkAsync(snapshotRecord, normalizedIterations, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? BenchmarkNetworkJsonManaged(string pluginId, string query)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(pluginId))
            {
                SetLastError("Plugin ID is required");
                return null;
            }

            var snapshotRecord = _registry!
                .GetSnapshot()
                .FirstOrDefault(r => string.Equals(r.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (snapshotRecord is null)
            {
                SetLastError($"Plugin '{pluginId}' not found");
                return null;
            }

            if (!_wasmRuntime!.IsWasmPlugin(snapshotRecord.Manifest))
            {
                SetLastError($"Plugin '{pluginId}' is not a WASM plugin.");
                return null;
            }

            var normalizedQuery = string.IsNullOrWhiteSpace(query)
                ? "one piece"
                : query.Trim();

            return _wasmRuntime.BenchmarkNetworkAsync(snapshotRecord, normalizedQuery, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    /// <summary>
    /// Search for media using the specified plugin and return typed results.
    /// Returns null on error, check GetLastErrorManaged().
    /// </summary>
    public static IReadOnlyList<EMMA.Domain.MediaSummary>? SearchMediaManaged(string pluginId, string query)
    {
        return SearchMediaManaged(pluginId, query, null);
    }

    public static IReadOnlyList<EMMA.Domain.MediaSummary>? SearchMediaManaged(string pluginId, string query, string? correlationId)
    {
        ClearLastError();
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var ensureInitStopwatch = System.Diagnostics.Stopwatch.StartNew();
            EnsureInitialized();
            ensureInitStopwatch.Stop();

            if (!TryResolvePlugin(pluginId, out var record, out var address))
            {
                return null;
            }

            var normalizedQuery = query ?? string.Empty;

            var runtimeSearchStopwatch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<EMMA.Domain.MediaSummary> results;
            if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
            {
                results = _wasmRuntime.SearchAsync(record, normalizedQuery, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
                {
                    SetLastError($"Unsupported plugin protocol: {record.Manifest.Protocol}");
                    return null;
                }

                if (address is null)
                {
                    SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
                    return null;
                }

                var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
                    ? Guid.NewGuid().ToString("n")
                    : correlationId;
                var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                var channel = GetOrCreateChannel(address);
                var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
                var headers = BuildGrpcHeaders(record.Manifest.Id, resolvedCorrelationId!);
                var response = client.SearchAsync(new PluginContracts.SearchRequest
                {
                    Query = normalizedQuery,
                    Context = new PluginContracts.RequestContext
                    {
                        CorrelationId = resolvedCorrelationId,
                        DeadlineUtc = deadlineUtc.ToString("O")
                    }
                }, headers: headers, cancellationToken: CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                results = response.Results
                    .Select(MapPluginSearchSummary)
                    .ToList();
            }

            runtimeSearchStopwatch.Stop();
            return results;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
        finally
        {
            totalStopwatch.Stop();
        }
    }

    private static bool TryResolveWasmSearchRecord(string pluginId, out PluginRecord? record)
    {
        record = null;

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            SetLastError("Plugin ID is required");
            return false;
        }

        var snapshotLookupStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var snapshotRecord = _registry!
            .GetSnapshot()
            .FirstOrDefault(r => string.Equals(r.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        snapshotLookupStopwatch.Stop();

        if (snapshotRecord is null)
        {
            SetLastError($"Plugin '{pluginId}' not found");
            return false;
        }

        if (!_wasmRuntime!.IsWasmPlugin(snapshotRecord.Manifest))
        {
            SetLastError($"Plugin '{pluginId}' is not a WASM plugin.");
            return false;
        }

        var runtimeState = snapshotRecord.Runtime.State;
        var searchReady = runtimeState is PluginRuntimeState.Running or PluginRuntimeState.External
            && (runtimeState == PluginRuntimeState.External || snapshotRecord.Status.Success);
        if (!searchReady)
        {
            SetLastError(
                $"Plugin '{pluginId}' is not ready for WASM search. " +
                $"runtimeState={snapshotRecord.Runtime.State}, " +
                $"runtimeCode={snapshotRecord.Runtime.LastErrorCode ?? "none"}, " +
                $"runtimeMessage={snapshotRecord.Runtime.LastErrorMessage ?? "none"}, " +
                $"handshakeSuccess={snapshotRecord.Status.Success}, " +
                $"handshakeMessage={snapshotRecord.Status.Message}");
            return false;
        }

        record = snapshotRecord;
        return true;
    }

    public static string? TakeLastSearchTimingManaged()
    {
        lock (_searchTimingLock)
        {
            var value = _lastSearchTiming;
            _lastSearchTiming = null;
            return value;
        }
    }

    public static string? GetChaptersJsonManaged(string pluginId, string mediaId)
    {
        ClearLastError();

        try
        {
            var chapters = GetChaptersManagedInternal(pluginId, mediaId, forceRefresh: false);
            if (chapters is null)
            {
                return null;
            }

            return JsonSerializer.Serialize(chapters, PluginHostExportsJsonContext.Default.IReadOnlyListMediaChapter);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    private static IReadOnlyList<MediaChapter>? GetChaptersManagedInternal(
        string pluginId,
        string mediaId,
        bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            SetLastError("Media ID is required");
            return null;
        }

        EnsureInitialized();
        var catalog = _serviceProvider!.GetRequiredService<IMediaCatalogPort>();
        var mediaKey = MediaId.Create(mediaId);

        if (!forceRefresh)
        {
            var cachedRecords = catalog.GetChaptersAsync(mediaKey, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (cachedRecords.Count > 0)
            {
                var hasAnyUploaderGroups = cachedRecords.Any(chapter =>
                    chapter.UploaderGroups is { Count: > 0 }
                    && chapter.UploaderGroups.Any(group => !string.IsNullOrWhiteSpace(group)));

                var hasNonGuidUploaderGroup = cachedRecords.Any(chapter =>
                    chapter.UploaderGroups is { Count: > 0 }
                    && chapter.UploaderGroups.Any(group =>
                        !string.IsNullOrWhiteSpace(group)
                        && !Guid.TryParse(group.Trim(), out _)));

                if (hasAnyUploaderGroups && hasNonGuidUploaderGroup)
                {
                    return cachedRecords
                        .Select(chapter => new MediaChapter(
                            chapter.ChapterId,
                            chapter.Number,
                            chapter.Title,
                            chapter.UploaderGroups ?? []))
                        .ToList();
                }
            }
        }

        if (!TryResolvePlugin(pluginId, out var record, out var address))
        {
            return null;
        }

        IReadOnlyList<MediaChapter> chapters;
        if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
        {
            chapters = _wasmRuntime.GetChaptersAsync(record, MediaId.Create(mediaId), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        else
        {
            if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                SetLastError($"Unsupported plugin protocol: {record.Manifest.Protocol}");
                return null;
            }

            if (address is null)
            {
                SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
                return null;
            }

            var channel = GetOrCreateChannel(address);
            var client = new PluginContracts.PageProvider.PageProviderClient(channel);
            var correlationId = Guid.NewGuid().ToString("n");
            var headers = BuildGrpcHeaders(record.Manifest.Id, correlationId);
            var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            var response = client.GetChaptersAsync(new PluginContracts.ChaptersRequest
            {
                MediaId = mediaId,
                Context = new PluginContracts.RequestContext
                {
                    CorrelationId = correlationId,
                    DeadlineUtc = deadlineUtc.ToString("O")
                }
            }, headers: headers, cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            chapters = response.Chapters
                .Select(chapter => new MediaChapter(
                    chapter.Id ?? string.Empty,
                    chapter.Number,
                    chapter.Title ?? string.Empty,
                    chapter.UploaderGroups
                        .Where(group => !string.IsNullOrWhiteSpace(group))
                        .Select(group => group.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .ToList();
        }

        var chapterRecords = chapters
            .Select(chapter => new MediaChapterRecord(
                chapter.ChapterId,
                mediaKey,
                chapter.Number,
                chapter.Title,
                null,
                chapter.UploaderGroups ?? []))
            .ToList();

        catalog.UpsertChaptersAsync(mediaKey, chapterRecords, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return chapters;
    }

    public static string? GetPageJsonManaged(string pluginId, string mediaId, string chapterId, int pageIndex)
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                SetLastError("Media ID and chapter ID are required");
                return null;
            }

            if (pageIndex < 0)
            {
                SetLastError("Page index must be >= 0");
                return null;
            }

            var page = GetPageManagedInternal(pluginId, mediaId, chapterId, pageIndex);
            if (page is null)
            {
                return null;
            }

            return JsonSerializer.Serialize(page, PluginHostExportsJsonContext.Default.MediaPage);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? GetPagesJsonManaged(string pluginId, string mediaId, string chapterId, int startIndex, int count)
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                SetLastError("Media ID and chapter ID are required");
                return null;
            }

            if (startIndex < 0)
            {
                SetLastError("startIndex must be >= 0");
                return null;
            }

            if (count <= 0)
            {
                SetLastError("count must be > 0");
                return null;
            }

            var pages = GetPagesManagedInternal(pluginId, mediaId, chapterId, startIndex, count);
            return JsonSerializer.Serialize(pages, PluginHostExportsJsonContext.Default.MediaPagesResult);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? GetPageAssetJsonManaged(string pluginId, string mediaId, string chapterId, int pageIndex)
    {
        ClearLastError();

        try
        {
            var asset = GetPageAssetManagedInternal(pluginId, mediaId, chapterId, pageIndex);
            if (asset is null)
            {
                return null;
            }

            return JsonSerializer.Serialize(asset, PluginHostExportsJsonContext.Default.MediaPageAsset);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? GetVideoStreamsJsonManaged(string pluginId, string mediaId)
    {
        ClearLastError();

        try
        {
            var streams = GetVideoStreamsManagedInternal(pluginId, mediaId);
            if (streams is null)
            {
                return null;
            }

            return JsonSerializer.Serialize(
                new VideoStreamsResponse(streams),
                PluginHostExportsJsonContext.Default.VideoStreamsResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    private static MediaPageAsset? GetPageAssetManagedInternal(string pluginId, string mediaId, string chapterId, int pageIndex)
    {
        if (TryReadDownloadedPagedAsset(pluginId, mediaId, chapterId, pageIndex, out var downloadedAsset))
        {
            return downloadedAsset;
        }

        var page = GetPageManagedInternal(pluginId, mediaId, chapterId, pageIndex);
        if (page is null)
        {
            return null;
        }

        EnsureInitialized();
        var cache = _serviceProvider!.GetService<IPageAssetCachePort>();
        var fetcher = _serviceProvider!.GetService<IPageAssetFetcherPort>();
        if (fetcher is null)
        {
            SetLastError("Page asset fetcher is not configured.");
            return null;
        }

        var cacheKey = $"page-asset:{page.ContentUri}";
        var cached = cache?.GetAsync(cacheKey, CancellationToken.None).GetAwaiter().GetResult();
        var asset = cached ?? fetcher.FetchAsync(page.ContentUri, CancellationToken.None).GetAwaiter().GetResult();

        if (cache is not null && cached is null)
        {
            cache.SetAsync(cacheKey, asset, CancellationToken.None).GetAwaiter().GetResult();
        }

        return asset;
    }

    private static int ChangeDownloadStateManaged(
        string jobId,
        Func<DownloadOrchestrator, string, Task<bool>> action)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var orchestrator = _downloadOrchestrator
                ?? throw new InvalidOperationException("Download orchestrator is not initialized.");

            var ok = action(orchestrator, jobId)
                .GetAwaiter()
                .GetResult();

            if (!ok)
            {
                SetLastError($"Failed to update download job '{jobId}'.");
                return 0;
            }

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    private static DownloadJobResponse MapDownloadJobResponse(DownloadJobRecord record)
    {
        return new DownloadJobResponse(
            record.Id,
            record.PluginId,
            record.MediaId,
            record.MediaType,
            record.ChapterId,
            record.StreamId,
            record.State.ToString(),
            record.ProgressCompleted,
            record.ProgressTotal,
            record.BytesDownloaded,
            record.ErrorMessage,
            record.CreatedAtUtc.ToString("O"),
            record.UpdatedAtUtc.ToString("O"),
            record.StartedAtUtc?.ToString("O"),
            record.CompletedAtUtc?.ToString("O"));
    }

    private static async Task<DownloadExecutionResult> ExecuteDownloadJobAsync(
        DownloadJobRecord job,
        IProgress<DownloadExecutionProgress> progress,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var normalizedType = (job.MediaType ?? string.Empty).Trim().ToLowerInvariant();
        return normalizedType switch
        {
            "video" => await ExecuteVideoDownloadJobAsync(job, progress, cancellationToken),
            _ => await ExecutePagedDownloadJobAsync(job, progress, cancellationToken)
        };
    }

    private static Task<DownloadExecutionResult> ExecutePagedDownloadJobAsync(
        DownloadJobRecord job,
        IProgress<DownloadExecutionProgress> progress,
        CancellationToken cancellationToken)
    {
        var chapters = GetChaptersManagedInternal(job.PluginId, job.MediaId, forceRefresh: false);
        if (chapters is null)
        {
            return Task.FromResult(new DownloadExecutionResult(
                false,
                0,
                0,
                0,
                GetLastErrorManaged() ?? "Failed to resolve chapters for download."));
        }

        var selectedChapters = !string.IsNullOrWhiteSpace(job.ChapterId)
            ? chapters.Where(ch => string.Equals(ch.ChapterId, job.ChapterId, StringComparison.Ordinal)).ToList()
            : chapters.ToList();

        if (selectedChapters.Count == 0)
        {
            return Task.FromResult(new DownloadExecutionResult(false, 0, 0, 0, "No chapters available for download."));
        }

        var storageRoot = ResolveDownloadRootDirectory();
        var completed = 0;
        var total = 0;
        long bytesDownloaded = 0;

        foreach (var chapter in selectedChapters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var startIndex = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pagesResult = GetPagesManagedInternal(
                    job.PluginId,
                    job.MediaId,
                    chapter.ChapterId,
                    startIndex,
                    64);

                if (pagesResult.Pages.Count == 0)
                {
                    break;
                }

                total += pagesResult.Pages.Count;

                foreach (var page in pagesResult.Pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var asset = GetPageAssetManagedInternal(job.PluginId, job.MediaId, chapter.ChapterId, page.Index);
                    if (asset is null)
                    {
                        return Task.FromResult(new DownloadExecutionResult(
                            false,
                            completed,
                            total,
                            bytesDownloaded,
                            GetLastErrorManaged() ?? "Failed to fetch page asset."));
                    }

                    var pagePath = Path.Combine(
                        storageRoot,
                        "paged",
                        SanitizePathSegment(job.PluginId),
                        SanitizePathSegment(job.MediaId),
                        SanitizePathSegment(chapter.ChapterId),
                        $"{page.Index:D6}.bin");

                    WriteDownloadPayload(pagePath, asset.Payload);
                    bytesDownloaded += asset.Payload.LongLength;
                    completed++;
                    progress.Report(new DownloadExecutionProgress(completed, total, bytesDownloaded));
                }

                if (pagesResult.ReachedEnd)
                {
                    break;
                }

                startIndex += pagesResult.Pages.Count;
            }
        }

        return Task.FromResult(new DownloadExecutionResult(true, completed, total, bytesDownloaded, null));
    }

    private static Task<DownloadExecutionResult> ExecuteVideoDownloadJobAsync(
        DownloadJobRecord job,
        IProgress<DownloadExecutionProgress> progress,
        CancellationToken cancellationToken)
    {
        var streams = GetVideoStreamsManagedInternal(job.PluginId, job.MediaId);
        if (streams is null || streams.Count == 0)
        {
            return Task.FromResult(new DownloadExecutionResult(
                false,
                0,
                0,
                0,
                GetLastErrorManaged() ?? "No video streams available for download."));
        }

        var selectedStream = !string.IsNullOrWhiteSpace(job.StreamId)
            ? streams.FirstOrDefault(stream => string.Equals(stream.Id, job.StreamId, StringComparison.Ordinal))
            : streams.First();
        if (selectedStream is null)
        {
            return Task.FromResult(new DownloadExecutionResult(false, 0, 0, 0, "Requested stream was not found."));
        }

        var storageRoot = ResolveDownloadRootDirectory();
        var completed = 0;
        var total = 0;
        long bytesDownloaded = 0;

        if (TryResolveDirectVideoUri(selectedStream.PlaylistUri, out var directVideoUri, out var directVideoExtension))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = 1;

            try
            {
                byte[] payload;
                if (directVideoUri.IsFile)
                {
                    payload = File.ReadAllBytes(directVideoUri.LocalPath);
                }
                else
                {
                    using var httpClient = new HttpClient();
                    payload = httpClient.GetByteArrayAsync(directVideoUri, cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                }

                if (payload.Length == 0)
                {
                    return Task.FromResult(new DownloadExecutionResult(
                        false,
                        0,
                        total,
                        0,
                        "Direct video stream returned an empty payload."));
                }

                var directPath = BuildDownloadedVideoDirectFilePath(
                    job.PluginId,
                    job.MediaId,
                    selectedStream.Id,
                    directVideoExtension);
                WriteDownloadPayload(directPath, payload);
                bytesDownloaded = payload.LongLength;
                completed = 1;
                progress.Report(new DownloadExecutionProgress(completed, total, bytesDownloaded));
                return Task.FromResult(new DownloadExecutionResult(true, completed, total, bytesDownloaded, null));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new DownloadExecutionResult(
                    false,
                    0,
                    total,
                    0,
                    $"Failed to download direct video stream: {ex.Message}"));
            }
        }

        for (var sequence = 0; sequence < 100_000; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total++;

            var segment = GetVideoSegmentManagedInternal(job.PluginId, job.MediaId, selectedStream.Id, sequence);
            if (segment is null)
            {
                var error = GetLastErrorManaged();
                if (!string.IsNullOrWhiteSpace(error)
                    && error.StartsWith("SEGMENT_NOT_FOUND:", StringComparison.Ordinal))
                {
                    total--;
                    break;
                }

                return Task.FromResult(new DownloadExecutionResult(
                    false,
                    completed,
                    total,
                    bytesDownloaded,
                    error ?? "Failed to fetch video segment."));
            }

            var segmentPath = Path.Combine(
                storageRoot,
                "video",
                SanitizePathSegment(job.PluginId),
                SanitizePathSegment(job.MediaId),
                SanitizePathSegment(selectedStream.Id),
                $"{sequence:D6}.bin");

            WriteDownloadPayload(segmentPath, segment.Payload);
            bytesDownloaded += segment.Payload.LongLength;
            completed++;
            progress.Report(new DownloadExecutionProgress(completed, total, bytesDownloaded));
        }

        if (completed <= 0)
        {
            var error = GetLastErrorManaged();
            return Task.FromResult(new DownloadExecutionResult(
                false,
                completed,
                total,
                bytesDownloaded,
                string.IsNullOrWhiteSpace(error)
                    ? "No video segments were downloaded."
                    : error));
        }

        return Task.FromResult(new DownloadExecutionResult(true, completed, total, bytesDownloaded, null));
    }

    private static string ResolveDownloadRootDirectory()
    {
        EnsureInitialized();
        var storage = _serviceProvider!.GetRequiredService<StorageOptions>();
        var dbDirectory = Path.GetDirectoryName(storage.DatabasePath);
        if (string.IsNullOrWhiteSpace(dbDirectory))
        {
            dbDirectory = Path.Combine(Path.GetTempPath(), "EMMA");
        }

        var root = Path.Combine(dbDirectory, "downloads");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteDownloadPayload(string path, byte[] payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, payload ?? Array.Empty<byte>());
    }

    private static string BuildDownloadedPagedAssetPath(string pluginId, string mediaId, string chapterId, int pageIndex)
    {
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "paged",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId),
            SanitizePathSegment(chapterId),
            $"{pageIndex:D6}.bin");
    }

    private static string BuildDownloadedPagedChapterRootPath(string pluginId, string mediaId, string chapterId)
    {
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "paged",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId),
            SanitizePathSegment(chapterId));
    }

    private static bool TryGetDownloadedPagedPage(
        string pluginId,
        string mediaId,
        string chapterId,
        int pageIndex,
        out MediaPage? page)
    {
        page = null;
        var path = BuildDownloadedPagedAssetPath(pluginId, mediaId, chapterId, pageIndex);
        if (!File.Exists(path))
        {
            return false;
        }

        page = new MediaPage(
            $"offline:{chapterId}:{pageIndex}",
            pageIndex,
            new Uri(
                $"emma-offline://paged/{Uri.EscapeDataString(pluginId)}/{Uri.EscapeDataString(mediaId)}/{Uri.EscapeDataString(chapterId)}/{pageIndex:D6}.bin",
                UriKind.Absolute));
        return true;
    }

    private static bool TryGetDownloadedPagedPages(
        string pluginId,
        string mediaId,
        string chapterId,
        int startIndex,
        int count,
        out MediaPagesResult result)
    {
        result = new MediaPagesResult([], true);

        var chapterRoot = BuildDownloadedPagedChapterRootPath(pluginId, mediaId, chapterId);
        if (!Directory.Exists(chapterRoot))
        {
            return false;
        }

        var pageIndexes = new List<int>();
        foreach (var filePath in Directory.EnumerateFiles(chapterRoot, "*.bin"))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (int.TryParse(fileName, out var parsedIndex) && parsedIndex >= 0)
            {
                pageIndexes.Add(parsedIndex);
            }
        }

        if (pageIndexes.Count == 0)
        {
            return false;
        }

        pageIndexes.Sort();

        var pages = pageIndexes
            .Where(index => index >= startIndex)
            .Take(count)
            .Select(index =>
                new MediaPage(
                    $"offline:{chapterId}:{index}",
                    index,
                    new Uri(
                        $"emma-offline://paged/{Uri.EscapeDataString(pluginId)}/{Uri.EscapeDataString(mediaId)}/{Uri.EscapeDataString(chapterId)}/{index:D6}.bin",
                        UriKind.Absolute)))
            .ToList();

        if (pages.Count == 0)
        {
            result = new MediaPagesResult([], true);
            return true;
        }

        var maxIndex = pageIndexes[^1];
        var reachedEnd = pages[^1].Index >= maxIndex;
        result = new MediaPagesResult(pages, reachedEnd);
        return true;
    }

    private static string BuildDownloadedVideoSegmentPath(string pluginId, string mediaId, string streamId, int sequence)
    {
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "video",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId),
            SanitizePathSegment(streamId),
            $"{sequence:D6}.bin");
    }

    private static string BuildDownloadedVideoDirectFilePath(string pluginId, string mediaId, string streamId, string extension)
    {
        var normalizedExtension = NormalizeDirectVideoExtension(extension);
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "video",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId),
            SanitizePathSegment(streamId),
            $"source{normalizedExtension}");
    }

    private static string BuildDownloadedPagedRootPath(string pluginId, string mediaId)
    {
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "paged",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId));
    }

    private static string BuildDownloadedVideoRootPath(string pluginId, string mediaId)
    {
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "video",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId));
    }

    private static string BuildDownloadedVideoPlaylistPath(string pluginId, string mediaId, string streamId)
    {
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "video",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId),
            SanitizePathSegment(streamId),
            "offline.m3u8");
    }

    private static void DeleteDownloadedArtifactsForJob(DownloadJobRecord job)
    {
        var mediaType = (job.MediaType ?? string.Empty).Trim().ToLowerInvariant();
        switch (mediaType)
        {
            case "video":
                {
                    var mediaRoot = BuildDownloadedVideoRootPath(job.PluginId, job.MediaId);
                    if (string.IsNullOrWhiteSpace(job.StreamId))
                    {
                        DeleteDirectoryIfExists(mediaRoot);
                        return;
                    }

                    var streamRoot = Path.Combine(mediaRoot, SanitizePathSegment(job.StreamId));
                    DeleteDirectoryIfExists(streamRoot);
                    DeleteDirectoryIfEmpty(mediaRoot);
                    return;
                }
            default:
                {
                    var mediaRoot = BuildDownloadedPagedRootPath(job.PluginId, job.MediaId);
                    if (string.IsNullOrWhiteSpace(job.ChapterId))
                    {
                        DeleteDirectoryIfExists(mediaRoot);
                        return;
                    }

                    var chapterRoot = Path.Combine(mediaRoot, SanitizePathSegment(job.ChapterId));
                    DeleteDirectoryIfExists(chapterRoot);
                    DeleteDirectoryIfEmpty(mediaRoot);
                    return;
                }
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            return;
        }

        Directory.Delete(path, recursive: false);
    }

    private static bool TryReadDownloadedPagedAsset(
        string pluginId,
        string mediaId,
        string chapterId,
        int pageIndex,
        out MediaPageAsset? asset)
    {
        asset = null;
        var path = BuildDownloadedPagedAssetPath(pluginId, mediaId, chapterId, pageIndex);
        if (!File.Exists(path))
        {
            return false;
        }

        asset = new MediaPageAsset(
            "application/octet-stream",
            File.ReadAllBytes(path),
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        return true;
    }

    private static bool TryReadDownloadedVideoSegmentAsset(
        string pluginId,
        string mediaId,
        string streamId,
        int sequence,
        out VideoSegmentAssetResponse? asset)
    {
        asset = null;
        var path = BuildDownloadedVideoSegmentPath(pluginId, mediaId, streamId, sequence);
        if (!File.Exists(path))
        {
            return false;
        }

        asset = new VideoSegmentAssetResponse(
            "application/octet-stream",
            File.ReadAllBytes(path),
            File.GetLastWriteTimeUtc(path).ToString("O"));
        return true;
    }

    private static IReadOnlyList<VideoStreamResponse> ReadDownloadedVideoStreams(string pluginId, string mediaId)
    {
        var mediaRoot = Path.Combine(
            ResolveDownloadRootDirectory(),
            "video",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId));
        if (!Directory.Exists(mediaRoot))
        {
            return [];
        }

        var streams = new List<VideoStreamResponse>();
        foreach (var streamDirectory in Directory.EnumerateDirectories(mediaRoot))
        {
            var streamId = Path.GetFileName(streamDirectory);
            if (string.IsNullOrWhiteSpace(streamId))
            {
                continue;
            }

            var directVideoFile = TryGetDownloadedDirectVideoFile(streamDirectory);
            if (directVideoFile is not null)
            {
                streams.Add(new VideoStreamResponse(
                    streamId,
                    $"Offline {streamId}",
                    new Uri(directVideoFile).AbsoluteUri));
                continue;
            }

            if (!Directory.EnumerateFiles(streamDirectory, "*.bin").Any())
            {
                continue;
            }

            if (LooksLikeSyntheticTextSegments(streamDirectory))
            {
                continue;
            }

            var playlistPath = BuildDownloadedVideoPlaylistPath(pluginId, mediaId, streamId);
            if (!TryWriteOfflineVideoPlaylist(streamDirectory, playlistPath))
            {
                continue;
            }

            streams.Add(new VideoStreamResponse(
                streamId,
                $"Offline {streamId}",
                new Uri(playlistPath).AbsoluteUri));
        }

        return streams;
    }

    private static bool TryWriteOfflineVideoPlaylist(string streamDirectory, string playlistPath)
    {
        var segments = Directory.EnumerateFiles(streamDirectory, "*.bin")
            .Select(path => new
            {
                Path = path,
                Name = Path.GetFileName(path),
                Sort = ParseSequenceFromPath(path)
            })
            .Where(item => item.Sort >= 0 && !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Sort)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();

        if (segments.Count == 0)
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:3");
        sb.AppendLine("#EXT-X-TARGETDURATION:6");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        foreach (var segment in segments)
        {
            sb.AppendLine("#EXTINF:6.000,");
            sb.AppendLine(segment.Name);
        }
        sb.AppendLine("#EXT-X-ENDLIST");

        var parent = Path.GetDirectoryName(playlistPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(playlistPath, sb.ToString());
        return true;
    }

    private static int ParseSequenceFromPath(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return int.TryParse(name, out var parsed) && parsed >= 0
            ? parsed
            : -1;
    }

    private static bool TryResolveDirectVideoUri(string playlistUri, out Uri directUri, out string extension)
    {
        directUri = default!;
        extension = string.Empty;

        if (string.IsNullOrWhiteSpace(playlistUri)
            || !Uri.TryCreate(playlistUri.Trim(), UriKind.Absolute, out var parsedUri))
        {
            return false;
        }

        var scheme = parsedUri.Scheme?.Trim().ToLowerInvariant() ?? string.Empty;
        if (scheme is not "http" and not "https" and not "file")
        {
            return false;
        }

        var ext = Path.GetExtension(parsedUri.AbsolutePath ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ext))
        {
            return false;
        }

        var normalized = NormalizeDirectVideoExtension(ext);
        if (normalized is not ".mp4" and not ".m4v" and not ".mov" and not ".webm" and not ".mkv" and not ".avi")
        {
            return false;
        }

        directUri = parsedUri;
        extension = normalized;
        return true;
    }

    private static string NormalizeDirectVideoExtension(string extension)
    {
        var normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ".mp4";
        }

        if (!normalized.StartsWith(".", StringComparison.Ordinal))
        {
            normalized = $".{normalized}";
        }

        return normalized;
    }

    private static string? TryGetDownloadedDirectVideoFile(string streamDirectory)
    {
        var candidates = Directory.EnumerateFiles(streamDirectory)
            .Where(path =>
            {
                var ext = Path.GetExtension(path).Trim().ToLowerInvariant();
                return ext is ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" or ".avi";
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static bool LooksLikeSyntheticTextSegments(string streamDirectory)
    {
        var firstSegmentPath = Directory.EnumerateFiles(streamDirectory, "*.bin")
            .Select(path => new
            {
                Path = path,
                Sort = ParseSequenceFromPath(path)
            })
            .Where(item => item.Sort >= 0)
            .OrderBy(item => item.Sort)
            .Select(item => item.Path)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstSegmentPath) || !File.Exists(firstSegmentPath))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(firstSegmentPath);
        if (bytes.Length == 0)
        {
            return true;
        }

        var probe = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 128));
        return probe.StartsWith("SEGMENT|", StringComparison.Ordinal);
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var sanitized = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    public static string? GetVideoSegmentJsonManaged(string pluginId, string mediaId, string streamId, int sequence)
    {
        ClearLastError();

        try
        {
            var segment = GetVideoSegmentManagedInternal(pluginId, mediaId, streamId, sequence);
            if (segment is null)
            {
                return null;
            }

            return JsonSerializer.Serialize(segment, PluginHostExportsJsonContext.Default.VideoSegmentAssetResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    /// <summary>
    /// Get the last error message, or null if no error.
    /// </summary>
    public static string? GetLastErrorManaged()
    {
        lock (_errorLock)
        {
            return _lastError;
        }
    }

    public static string? ListCatalogMediaJsonManaged(int limit = 500)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            var catalog = _serviceProvider!.GetRequiredService<IMediaCatalogPort>();
            var items = catalog.ListMediaAsync(Math.Max(1, limit), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var results = items
                .Select(item => new EMMA.Domain.MediaSummary(
                    item.Id,
                    item.SourceId,
                    item.Title,
                    item.MediaType,
                    null,
                    item.Synopsis))
                .ToList();

            return JsonSerializer.Serialize(results, PluginHostExportsJsonContext.Default.IReadOnlyListMediaSummary);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? ListLibraryMediaJsonManaged(string userId = "Library")
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            var normalizedUserId = ToLibraryStorageKey(userId);
            var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
            var catalog = _serviceProvider!.GetRequiredService<IMediaCatalogPort>();
            var entries = library.GetLibraryAsync(normalizedUserId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var results = new List<EMMA.Domain.MediaSummary>(entries.Count);
            foreach (var entry in entries)
            {
                var metadata = catalog.GetMediaAsync(entry.MediaId, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (metadata is null)
                {
                    results.Add(new EMMA.Domain.MediaSummary(
                        entry.MediaId,
                        entry.SourceId,
                        entry.MediaId.Value,
                        EMMA.Domain.MediaType.Paged));
                    continue;
                }

                results.Add(new EMMA.Domain.MediaSummary(
                    metadata.Id,
                    metadata.SourceId,
                    metadata.Title,
                    metadata.MediaType,
                    null,
                    metadata.Synopsis));
            }

            return JsonSerializer.Serialize(results, PluginHostExportsJsonContext.Default.IReadOnlyListMediaSummary);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? RefreshLibraryMediaJsonManaged(string libraryName = "Library")
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            var normalizedLibraryName = NormalizeLibraryDisplayName(libraryName);
            var normalizedUserId = ToLibraryStorageKey(normalizedLibraryName);

            var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
            var catalog = _serviceProvider!.GetRequiredService<IMediaCatalogPort>();
            var entries = library.GetLibraryAsync(normalizedUserId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var failures = new List<LibraryMediaRefreshFailure>();
            var updates = new List<LibraryMediaDiscoveredUpdate>();
            var refreshedItems = 0;
            var refreshedPagedItems = 0;
            var refreshedChapters = 0;
            var skippedItems = 0;

            foreach (var entry in entries)
            {
                var mediaId = entry.MediaId.Value;
                var metadata = catalog.GetMediaAsync(entry.MediaId, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                var sourceId = !string.IsNullOrWhiteSpace(metadata?.SourceId)
                    ? metadata!.SourceId
                    : entry.SourceId;

                if (string.IsNullOrWhiteSpace(sourceId))
                {
                    failures.Add(new LibraryMediaRefreshFailure(mediaId, null, "Missing source/plugin ID."));
                    continue;
                }

                var mediaType = metadata?.MediaType ?? MediaType.Paged;
                var refreshFailed = false;
                if (mediaType == MediaType.Paged)
                {
                    var knownChapterIds = catalog.GetChaptersAsync(entry.MediaId, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()
                        .Select(chapter => chapter.ChapterId)
                        .Where(chapterId => !string.IsNullOrWhiteSpace(chapterId))
                        .ToHashSet(StringComparer.Ordinal);

                    var chapters = GetChaptersManagedInternal(sourceId, mediaId, forceRefresh: true);
                    if (chapters is null)
                    {
                        var reason = GetLastErrorManaged() ?? "Failed to refresh chapters.";
                        failures.Add(new LibraryMediaRefreshFailure(mediaId, sourceId, reason));
                        refreshFailed = true;
                    }
                    else
                    {
                        refreshedPagedItems++;
                        refreshedChapters += chapters.Count;

                        var newItemsCount = chapters.Count(chapter => !knownChapterIds.Contains(chapter.ChapterId));
                        if (newItemsCount > 0)
                        {
                            var mediaTypeText = mediaType == MediaType.Video ? "video" : "paged";
                            var title = !string.IsNullOrWhiteSpace(metadata?.Title)
                                ? metadata!.Title
                                : mediaId;
                            updates.Add(new LibraryMediaDiscoveredUpdate(
                                mediaId,
                                sourceId,
                                title,
                                mediaTypeText,
                                newItemsCount));
                        }
                    }
                }
                else
                {
                    skippedItems++;
                }

                if (refreshFailed)
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                var metadataToPersist = metadata is null
                    ? new MediaMetadata(
                        entry.MediaId,
                        sourceId,
                        mediaId,
                        mediaType,
                        null,
                        null,
                        null,
                        [],
                        now,
                        now)
                    : metadata with { UpdatedAtUtc = now };

                catalog.UpsertMediaAsync(metadataToPersist, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                refreshedItems++;
            }

            var response = new LibraryMediaRefreshResponse(
                normalizedLibraryName,
                entries.Count,
                refreshedItems,
                refreshedPagedItems,
                refreshedChapters,
                skippedItems,
                failures.Count,
                failures,
                updates);

            return JsonSerializer.Serialize(response, PluginHostExportsJsonContext.Default.LibraryMediaRefreshResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? ListLibrariesJsonManaged()
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
            var keys = library.ListLibrariesAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var names = keys
                .Select(FromLibraryStorageKey)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return JsonSerializer.Serialize(names, PluginHostExportsJsonContext.Default.IReadOnlyListString);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static int CreateLibraryManaged(string libraryName)
    {
        ClearLastError();

        try
        {
            var normalizedName = NormalizeLibraryDisplayName(libraryName);

            EnsureInitialized();
            var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
            library.CreateLibraryAsync(
                ToLibraryStorageKey(normalizedName),
                normalizedName,
                CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static int DeleteLibraryManaged(string libraryName)
    {
        ClearLastError();

        try
        {
            var normalizedName = NormalizeLibraryDisplayName(libraryName);
            if (string.Equals(normalizedName, "Library", StringComparison.OrdinalIgnoreCase))
            {
                SetLastError("The default Library cannot be deleted.");
                return 0;
            }

            EnsureInitialized();
            var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
            library.DeleteLibraryAsync(
                    ToLibraryStorageKey(normalizedName),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static int ResetDatabaseManaged()
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            var storageOptions = _serviceProvider!.GetRequiredService<StorageOptions>();
            var dbPath = storageOptions.DatabasePath;

            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            var walPath = dbPath + "-wal";
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
            }

            var shmPath = dbPath + "-shm";
            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }

            var storageInit = _serviceProvider!.GetRequiredService<StorageInitializer>();
            storageInit.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            EnsureDefaultLibraryExistsManaged();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static bool IsMediaInLibraryManaged(string mediaId, string userId = "*")
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Media ID is required");
                return false;
            }

            EnsureInitialized();
            var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
            if (string.IsNullOrWhiteSpace(userId) || string.Equals(userId, "*", StringComparison.Ordinal))
            {
                var keys = library.ListLibrariesAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                foreach (var key in keys)
                {
                    var entriesForLibrary = library.GetLibraryAsync(key, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    if (entriesForLibrary.Any(entry => string.Equals(entry.MediaId.Value, mediaId, StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }

                return false;
            }

            var normalizedUserId = ToLibraryStorageKey(userId);
            var entries = library.GetLibraryAsync(normalizedUserId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return entries.Any(entry => string.Equals(entry.MediaId.Value, mediaId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return false;
        }
    }

    public static int AddMediaToLibraryManaged(
        string mediaId,
        string sourceId,
        string title,
        string mediaType,
        string userId = "Library",
        string? description = null)
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Media ID is required");
                return 0;
            }

            EnsureInitialized();

            var parsedMediaType = string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase)
                ? MediaType.Video
                : MediaType.Paged;
            var now = DateTimeOffset.UtcNow;
            var normalizedUserId = ToLibraryStorageKey(userId);

            var mediaCatalog = _serviceProvider!.GetRequiredService<IMediaCatalogPort>();
            mediaCatalog.UpsertMediaAsync(
                new MediaMetadata(
                    MediaId.Create(mediaId),
                    sourceId ?? string.Empty,
                    title ?? string.Empty,
                    parsedMediaType,
                    null,
                    string.IsNullOrWhiteSpace(description) ? null : description,
                    null,
                    [],
                    now,
                    now),
                CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
            var entryId = $"{normalizedUserId}:{mediaId}";

            library.UpsertAsync(
                new LibraryEntry(
                    entryId,
                    MediaId.Create(mediaId),
                    normalizedUserId,
                    now,
                    sourceId ?? string.Empty),
                CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static int RemoveMediaFromLibraryManaged(string mediaId, string userId = "Library")
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Media ID is required");
                return 0;
            }

            EnsureInitialized();

            var normalizedUserId = ToLibraryStorageKey(userId);
            var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
            library.RemoveAsync(normalizedUserId, MediaId.Create(mediaId), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static string? GetMediaProgressJsonManaged(
        string mediaId,
        string pluginId,
        string mediaType,
        string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Media ID is required");
                return null;
            }

            EnsureInitialized();
            var progress = _serviceProvider!.GetRequiredService<IProgressPort>();
            var mediaIdValue = MediaId.Create(mediaId);
            var normalizedPluginId = pluginId ?? string.Empty;
            var normalizedUserId = string.IsNullOrWhiteSpace(userId)
                ? DefaultProgressUserId
                : userId;

            if (string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase))
            {
                var video = progress.GetVideoProgressAsync(
                    mediaIdValue,
                    normalizedPluginId,
                    normalizedUserId,
                    CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (video is null)
                {
                    return "null";
                }

                var payload = new MediaProgressResponse(
                    "video",
                    null,
                    null,
                    video.PositionSeconds,
                    video.Completed,
                    video.LastViewedAtUtc.ToString("O"));
                return JsonSerializer.Serialize(payload, PluginHostExportsJsonContext.Default.MediaProgressResponse);
            }

            var paged = progress.GetPagedProgressAsync(
                mediaIdValue,
                normalizedPluginId,
                normalizedUserId,
                CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (paged is null)
            {
                return "null";
            }

            var pagedPayload = new MediaProgressResponse(
                "paged",
                paged.ChapterId,
                paged.PageIndex,
                null,
                paged.Completed,
                paged.LastViewedAtUtc.ToString("O"));
            return JsonSerializer.Serialize(pagedPayload, PluginHostExportsJsonContext.Default.MediaProgressResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static int SetPagedProgressManaged(
        string mediaId,
        string pluginId,
        string chapterId,
        int pageIndex,
        bool completed,
        string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Media ID is required");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(chapterId))
            {
                SetLastError("Chapter ID is required");
                return 0;
            }

            EnsureInitialized();
            var progress = _serviceProvider!.GetRequiredService<IProgressPort>();
            var normalizedUserId = string.IsNullOrWhiteSpace(userId)
                ? DefaultProgressUserId
                : userId;

            progress.SetPagedProgressAsync(
                MediaId.Create(mediaId),
                pluginId ?? string.Empty,
                chapterId,
                Math.Max(0, pageIndex),
                completed,
                normalizedUserId,
                CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static int SetVideoProgressManaged(
        string mediaId,
        string pluginId,
        double positionSeconds,
        bool completed,
        string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Media ID is required");
                return 0;
            }

            EnsureInitialized();
            var progress = _serviceProvider!.GetRequiredService<IProgressPort>();
            var normalizedUserId = string.IsNullOrWhiteSpace(userId)
                ? DefaultProgressUserId
                : userId;

            progress.SetVideoProgressAsync(
                MediaId.Create(mediaId),
                pluginId ?? string.Empty,
                Math.Max(0, positionSeconds),
                completed,
                normalizedUserId,
                CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    public static string? GetReadChapterIdsJsonManaged(
        string mediaId,
        string pluginId,
        string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Media ID is required");
                return null;
            }

            EnsureInitialized();
            var history = _serviceProvider!.GetRequiredService<IHistoryPort>();
            var normalizedPluginId = pluginId ?? string.Empty;
            var normalizedUserId = string.IsNullOrWhiteSpace(userId)
                ? DefaultProgressUserId
                : userId;

            var entries = history.GetHistoryAsync(normalizedUserId, 10000, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var chapterIds = entries
                .Where(entry => entry.MediaId.Value == mediaId)
                .Where(entry => string.Equals(entry.PluginId, normalizedPluginId, StringComparison.Ordinal))
                .Where(entry => entry.EntryId.StartsWith("paged::", StringComparison.Ordinal))
                .Select(entry => entry.ExternalId)
                .Where(externalId => !string.IsNullOrWhiteSpace(externalId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            IReadOnlyList<string> payload = chapterIds;
            return JsonSerializer.Serialize(payload, PluginHostExportsJsonContext.Default.IReadOnlyListString);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? GetHistoryJsonManaged(
        int limit = 200,
        string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();
            var history = _serviceProvider!.GetRequiredService<IHistoryPort>();
            var normalizedUserId = string.IsNullOrWhiteSpace(userId)
                ? DefaultProgressUserId
                : userId;

            var entries = history.GetHistoryAsync(normalizedUserId, Math.Max(1, limit), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var payload = entries
                .Select(entry => new HistoryEntryResponse(
                    entry.EntryId,
                    entry.MediaId.Value,
                    entry.PluginId,
                    entry.ExternalId,
                    entry.UserId,
                    entry.Position,
                    entry.Completed,
                    entry.LastViewedAtUtc.ToString("O")))
                .ToList();

            IReadOnlyList<HistoryEntryResponse> typedPayload = payload;
            return JsonSerializer.Serialize(
                typedPayload,
                PluginHostExportsJsonContext.Default.IReadOnlyListHistoryEntryResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static int DeleteHistoryForMediaManaged(
        string mediaId,
        string pluginId,
        string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Media ID is required");
                return 0;
            }

            EnsureInitialized();
            var history = _serviceProvider!.GetRequiredService<IHistoryPort>();
            var normalizedUserId = string.IsNullOrWhiteSpace(userId)
                ? DefaultProgressUserId
                : userId;

            history.DeleteForMediaAsync(
                    MediaId.Create(mediaId),
                    pluginId ?? string.Empty,
                    normalizedUserId,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    // ==================== FFI Boundary (UnmanagedCallersOnly) ====================

    [UnmanagedCallersOnly(EntryPoint = "plugin_host_initialize")]
    public static int Initialize(IntPtr manifestsDirUtf8, IntPtr sandboxDirUtf8)
    {
        var manifestsDir = PtrToString(manifestsDirUtf8) ?? "manifests";
        var sandboxDir = PtrToString(sandboxDirUtf8) ?? "sandbox";
        return InitializeManaged(manifestsDir, sandboxDir);
    }

    [UnmanagedCallersOnly(EntryPoint = "plugin_host_shutdown")]
    public static void Shutdown()
    {
        ShutdownManaged();
    }

    [UnmanagedCallersOnly(EntryPoint = "plugin_host_list_plugins_json")]
    public static IntPtr ListPluginsJson()
    {
        var json = ListPluginsJsonManaged();
        return json != null ? AllocUtf8(json) : IntPtr.Zero;
    }

    [UnmanagedCallersOnly(EntryPoint = "plugin_host_search_json")]
    public static IntPtr SearchJson(IntPtr pluginIdUtf8, IntPtr queryUtf8)
    {
        var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;
        var query = PtrToString(queryUtf8) ?? string.Empty;
        var json = SearchJsonManaged(pluginId, query);
        return json != null ? AllocUtf8(json) : IntPtr.Zero;
    }

    [UnmanagedCallersOnly(EntryPoint = "plugin_host_last_error")]
    public static IntPtr LastError()
    {
        var error = GetLastErrorManaged();
        return error != null ? AllocUtf8(error) : IntPtr.Zero;
    }

    [UnmanagedCallersOnly(EntryPoint = "plugin_host_string_free")]
    public static void StringFree(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    // ==================== Private helpers ====================

    private static void EnsureInitialized()
    {
        if (!_initialized || _registry == null || _pluginResolution == null || _wasmRuntime == null)
        {
            throw new InvalidOperationException("Plugin host not initialized. Call InitializeManaged first.");
        }
    }

    private static string ToLibraryStorageKey(string? libraryName)
    {
        var normalizedLibrary = NormalizeLibraryDisplayName(libraryName);
        return $"library::{normalizedLibrary}";
    }

    private static string NormalizeLibraryDisplayName(string? libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
        {
            return "Library";
        }

        var normalized = libraryName.Trim();
        return string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase)
            ? "Library"
            : normalized;
    }

    private static string FromLibraryStorageKey(string storageKey)
    {
        const string prefix = "library::";
        if (storageKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = storageKey[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(name)
                || string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
            {
                return "Library";
            }

            return name;
        }

        if (string.IsNullOrWhiteSpace(storageKey)
            || string.Equals(storageKey, "default", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storageKey, "library::default", StringComparison.OrdinalIgnoreCase))
        {
            return "Library";
        }

        return storageKey;
    }

    private static void EnsureDefaultLibraryExistsManaged()
    {
        var library = _serviceProvider!.GetRequiredService<ILibraryPort>();
        var canonicalDefault = ToLibraryStorageKey("Library");
        library.CreateLibraryAsync(
                canonicalDefault,
                "Library",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static MediaPage? GetPageManagedInternal(string pluginId, string mediaId, string chapterId, int pageIndex)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
        {
            SetLastError("Media ID and chapter ID are required");
            return null;
        }

        if (pageIndex < 0)
        {
            SetLastError("Page index must be >= 0");
            return null;
        }

        if (TryGetDownloadedPagedPage(pluginId, mediaId, chapterId, pageIndex, out var offlinePage))
        {
            return offlinePage;
        }

        if (!TryResolvePlugin(pluginId, out var record, out var address))
        {
            return null;
        }

        var pageCacheKey = BuildPageCacheKey(record!.Manifest.Id, mediaId, chapterId, pageIndex);
        if (_pageCache.TryGetValue(pageCacheKey, out var cachedPage))
        {
            return cachedPage;
        }

        if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
        {
            try
            {
                var wasmPage = _wasmRuntime.GetPageAsync(record, MediaId.Create(mediaId), chapterId, pageIndex, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                _pageCache[pageCacheKey] = wasmPage;
                return wasmPage;
            }
            catch (KeyNotFoundException ex) when (ex.Message.StartsWith("PAGE_NOT_FOUND:", StringComparison.Ordinal))
            {
                SetLastError(ex.Message);
                return null;
            }
        }

        if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            SetLastError($"Unsupported plugin protocol: {record.Manifest.Protocol}");
            return null;
        }

        if (address is null)
        {
            SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
            return null;
        }

        var channel = GetOrCreateChannel(address);
        var client = new PluginContracts.PageProvider.PageProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var headers = BuildGrpcHeaders(record.Manifest.Id, correlationId);
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        var response = client.GetPageAsync(new PluginContracts.PageRequest
        {
            MediaId = mediaId,
            ChapterId = chapterId,
            Index = pageIndex,
            Context = new PluginContracts.RequestContext
            {
                CorrelationId = correlationId,
                DeadlineUtc = deadlineUtc.ToString("O")
            }
        }, headers: headers, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var page = response.Page;
        if (page is null || string.IsNullOrWhiteSpace(page.ContentUri))
        {
            SetLastError($"PAGE_NOT_FOUND:{chapterId}:{pageIndex}");
            return null;
        }

        if (!Uri.TryCreate(page.ContentUri, UriKind.Absolute, out var contentUri))
        {
            SetLastError("Plugin returned an invalid page content URI.");
            return null;
        }

        var resolvedPage = new MediaPage(page.Id ?? string.Empty, page.Index, contentUri);
        _pageCache[pageCacheKey] = resolvedPage;
        return resolvedPage;
    }

    private static MediaPagesResult GetPagesManagedInternal(string pluginId, string mediaId, string chapterId, int startIndex, int count)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
        {
            SetLastError("Media ID and chapter ID are required");
            return new MediaPagesResult([], true);
        }

        if (startIndex < 0)
        {
            SetLastError("startIndex must be >= 0");
            return new MediaPagesResult([], true);
        }

        if (count <= 0)
        {
            SetLastError("count must be > 0");
            return new MediaPagesResult([], true);
        }

        if (TryGetDownloadedPagedPages(pluginId, mediaId, chapterId, startIndex, count, out var offlinePages))
        {
            return offlinePages;
        }

        if (!TryResolvePlugin(pluginId, out var record, out var address))
        {
            return new MediaPagesResult([], true);
        }

        if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
        {
            var wasmPages = _wasmRuntime.GetPagesAsync(
                    record,
                    MediaId.Create(mediaId),
                    chapterId,
                    startIndex,
                    count,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            foreach (var page in wasmPages.Pages)
            {
                _pageCache[BuildPageCacheKey(record.Manifest.Id, mediaId, chapterId, page.Index)] = page;
            }

            return wasmPages;
        }

        if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            SetLastError($"Unsupported plugin protocol: {record.Manifest.Protocol}");
            return new MediaPagesResult([], true);
        }

        if (address is null)
        {
            SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
            return new MediaPagesResult([], true);
        }

        var channel = GetOrCreateChannel(address);
        var client = new PluginContracts.PageProvider.PageProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var headers = BuildGrpcHeaders(record.Manifest.Id, correlationId);
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        var response = client.GetPagesAsync(new PluginContracts.PagesRequest
        {
            MediaId = mediaId,
            ChapterId = chapterId,
            StartIndex = startIndex,
            Count = count,
            Context = new PluginContracts.RequestContext
            {
                CorrelationId = correlationId,
                DeadlineUtc = deadlineUtc.ToString("O")
            }
        }, headers: headers, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var pages = new List<MediaPage>(response.Pages.Count);
        foreach (var item in response.Pages)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ContentUri))
            {
                continue;
            }

            if (!Uri.TryCreate(item.ContentUri, UriKind.Absolute, out var contentUri))
            {
                SetLastError("Plugin returned an invalid page content URI.");
                return new MediaPagesResult([], true);
            }

            var page = new MediaPage(item.Id ?? string.Empty, item.Index, contentUri);
            pages.Add(page);
            _pageCache[BuildPageCacheKey(record.Manifest.Id, mediaId, chapterId, page.Index)] = page;
        }

        return new MediaPagesResult(pages, response.ReachedEnd);
    }

    private static IReadOnlyList<VideoStreamResponse>? GetVideoStreamsManagedInternal(string pluginId, string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            SetLastError("Media ID is required");
            return null;
        }

        var downloadedStreams = ReadDownloadedVideoStreams(pluginId, mediaId);
        if (downloadedStreams.Count > 0)
        {
            return downloadedStreams;
        }

        if (!TryResolvePlugin(pluginId, out var record, out var address))
        {
            return null;
        }

        if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
        {
            var wasmStreams = _wasmRuntime.GetVideoStreamsAsync(record, MediaId.Create(mediaId), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return wasmStreams
                .Select(stream => new VideoStreamResponse(
                    stream.Id ?? string.Empty,
                    stream.Label ?? string.Empty,
                    stream.PlaylistUri ?? string.Empty))
                .ToList();
        }

        if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            SetLastError($"Unsupported plugin protocol: {record.Manifest.Protocol}");
            return null;
        }

        if (address is null)
        {
            SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
            return null;
        }

        var channel = GetOrCreateChannel(address);
        var client = new PluginContracts.VideoProvider.VideoProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var headers = BuildGrpcHeaders(record.Manifest.Id, correlationId);
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);

        var response = client.GetStreamsAsync(new PluginContracts.StreamRequest
        {
            MediaId = mediaId,
            Context = new PluginContracts.RequestContext
            {
                CorrelationId = correlationId,
                DeadlineUtc = deadlineUtc.ToString("O")
            }
        }, headers: headers, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return response.Streams
            .Select(stream => new VideoStreamResponse(
                stream.Id ?? string.Empty,
                stream.Label ?? string.Empty,
                stream.PlaylistUri ?? string.Empty))
            .ToList();
    }

    private static VideoSegmentAssetResponse? GetVideoSegmentManagedInternal(string pluginId, string mediaId, string streamId, int sequence)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(streamId))
        {
            SetLastError("Media ID and stream ID are required");
            return null;
        }

        if (sequence < 0)
        {
            SetLastError("sequence must be >= 0");
            return null;
        }

        if (TryReadDownloadedVideoSegmentAsset(pluginId, mediaId, streamId, sequence, out var downloadedSegment))
        {
            return downloadedSegment;
        }

        if (!TryResolvePlugin(pluginId, out var record, out var address))
        {
            return null;
        }

        if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
        {
            var wasmSegment = _wasmRuntime.GetVideoSegmentAsync(
                    record,
                    MediaId.Create(mediaId),
                    streamId,
                    sequence,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (wasmSegment is null)
            {
                SetLastError($"SEGMENT_NOT_FOUND:{mediaId}:{streamId}:{sequence}");
                return null;
            }

            if (wasmSegment.Payload.Length == 0 && string.IsNullOrWhiteSpace(wasmSegment.ContentType))
            {
                SetLastError($"SEGMENT_NOT_FOUND:{mediaId}:{streamId}:{sequence}");
                return null;
            }

            return new VideoSegmentAssetResponse(
                string.IsNullOrWhiteSpace(wasmSegment.ContentType)
                    ? "application/octet-stream"
                    : wasmSegment.ContentType,
                wasmSegment.Payload,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        if (!string.Equals(record.Manifest.Protocol, "grpc", StringComparison.OrdinalIgnoreCase))
        {
            SetLastError($"Unsupported plugin protocol: {record.Manifest.Protocol}");
            return null;
        }

        if (address is null)
        {
            SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
            return null;
        }

        var channel = GetOrCreateChannel(address);
        var client = new PluginContracts.VideoProvider.VideoProviderClient(channel);
        var correlationId = Guid.NewGuid().ToString("n");
        var headers = BuildGrpcHeaders(record.Manifest.Id, correlationId);
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);

        var response = client.GetSegmentAsync(new PluginContracts.SegmentRequest
        {
            MediaId = mediaId,
            StreamId = streamId,
            Sequence = sequence,
            Context = new PluginContracts.RequestContext
            {
                CorrelationId = correlationId,
                DeadlineUtc = deadlineUtc.ToString("O")
            }
        }, headers: headers, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var payload = response.Payload.ToByteArray();
        if (payload.Length == 0 && string.IsNullOrWhiteSpace(response.ContentType))
        {
            SetLastError($"SEGMENT_NOT_FOUND:{mediaId}:{streamId}:{sequence}");
            return null;
        }

        return new VideoSegmentAssetResponse(
            string.IsNullOrWhiteSpace(response.ContentType)
                ? "application/octet-stream"
                : response.ContentType,
            payload,
            DateTimeOffset.UtcNow.ToString("O"));
    }

    private static string BuildPageCacheKey(string pluginId, string mediaId, string chapterId, int pageIndex)
    {
        return $"{pluginId}\u001f{mediaId}\u001f{chapterId}\u001f{pageIndex}";
    }

    private static GrpcChannel GetOrCreateChannel(Uri address)
    {
        return _grpcChannelCache.GetOrAdd(address.ToString(), _ => GrpcChannel.ForAddress(address));
    }

    private static EMMA.Domain.MediaSummary MapPluginSearchSummary(PluginContracts.MediaSummary result)
    {
        var mediaType = string.Equals(result.MediaType, "video", StringComparison.OrdinalIgnoreCase)
            ? EMMA.Domain.MediaType.Video
            : EMMA.Domain.MediaType.Paged;

        var thumbnailUrl = string.IsNullOrWhiteSpace(result.ThumbnailUrl)
            ? null
            : result.ThumbnailUrl;

        var description = string.IsNullOrWhiteSpace(result.Description)
            ? null
            : result.Description;

        return new EMMA.Domain.MediaSummary(
            EMMA.Domain.MediaId.Create(result.Id ?? string.Empty),
            result.Source ?? string.Empty,
            result.Title ?? string.Empty,
            mediaType,
            thumbnailUrl,
            description);
    }

    private static Metadata BuildGrpcHeaders(string pluginId, string correlationId)
    {
        var headers = new Metadata
        {
            { "x-correlation-id", correlationId }
        };

        var token = _serviceProvider?
            .GetService<PluginProcessManager>()?
            .GetHostAuthToken(pluginId);

        if (!string.IsNullOrWhiteSpace(token))
        {
            headers.Add(HostAuthHeader, token);
        }

        return headers;
    }

    private static bool TryResolvePlugin(string pluginId, out PluginRecord? record, out Uri? address)
    {
        record = null;
        address = null;

        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(pluginId))
        {
            SetLastError("Plugin ID is required");
            return false;
        }

        var resolution = _pluginResolution!
            .ResolveAsync(pluginId, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        record = resolution.Record;
        if (record == null)
        {
            var snapshotRecord = _registry!
                .GetSnapshot()
                .FirstOrDefault(r => string.Equals(r.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));

            if (snapshotRecord is not null)
            {
                SetLastError(
                    $"Plugin '{pluginId}' resolution failed before record binding. " +
                    $"runtimeState={snapshotRecord.Runtime.State}, " +
                    $"runtimeCode={snapshotRecord.Runtime.LastErrorCode ?? "none"}, " +
                    $"runtimeMessage={snapshotRecord.Runtime.LastErrorMessage ?? "none"}, " +
                    $"handshakeSuccess={snapshotRecord.Status.Success}, " +
                    $"handshakeMessage={snapshotRecord.Status.Message}");
                return false;
            }

            SetLastError($"Plugin '{pluginId}' not found");
            return false;
        }

        if (resolution.Error is not null)
        {
            SetLastError(
                $"Plugin resolution failed for '{pluginId}'. " +
                $"runtimeState={record.Runtime.State}, " +
                $"runtimeCode={record.Runtime.LastErrorCode ?? "none"}, " +
                $"runtimeMessage={record.Runtime.LastErrorMessage ?? "none"}, " +
                $"handshakeSuccess={record.Status.Success}, " +
                $"handshakeMessage={record.Status.Message}");
            return false;
        }

        address = resolution.Address;
        return true;
    }

    private static void InitializeSqliteForEmbeddedHost()
    {
        if (OperatingSystem.IsLinux()
            || OperatingSystem.IsIOS()
            || OperatingSystem.IsMacOS()
            || OperatingSystem.IsMacCatalyst()
            || OperatingSystem.IsTvOS())
        {
            try
            {
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
                SQLitePCL.raw.FreezeProvider();
                return;
            }
            catch
            {
                // Fall back to bundled provider if dynamic provider is unavailable.
            }
        }

        SQLitePCL.Batteries_V2.Init();
    }

    private static string? PtrToString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.PtrToStringUTF8(ptr);
    }

    private static IntPtr AllocUtf8(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return IntPtr.Zero;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocCoTaskMem(bytes.Length + 1);

        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);

        return ptr;
    }

    private static void ClearLastError()
    {
        lock (_errorLock)
        {
            _lastError = null;
        }
    }

    private static void SetLastError(string error)
    {
        lock (_errorLock)
        {
            _lastError = error;
        }
    }

    private static void SetLastError(Exception ex)
    {
        lock (_errorLock)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        }
    }
}
