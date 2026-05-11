using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EMMA.Application.Ports;
using EMMA.Plugin.Common;
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
/// Request payload forwarded to ASP.NET plugin search enrichment endpoints.
/// </summary>
/// <param name="Items">The search items to enrich.</param>
public sealed record PluginDevEnrichSearchItemsRequest(IReadOnlyList<SearchItem> Items);

/// <summary>
/// Native FFI exports for embedding the PluginHost in-process.
/// This provides both a managed C# API and a thin FFI marshalling layer.
/// </summary>
public static class PluginHostExports
{
    /// <summary>
    /// Represents a media summary enriched for host-library consumers.
    /// </summary>
    /// <param name="Id">The media identifier.</param>
    /// <param name="SourceId">The provider-specific source identifier.</param>
    /// <param name="Source">The source name.</param>
    /// <param name="Title">The display title.</param>
    /// <param name="MediaType">The media type.</param>
    /// <param name="ThumbnailUrl">The optional thumbnail URL.</param>
    /// <param name="Description">The optional description.</param>
    /// <param name="Metadata">Optional metadata entries.</param>
    public sealed record EnrichedMediaSummaryResponse(
        string Id,
        string SourceId,
        string Source,
        string Title,
        string MediaType,
        string? ThumbnailUrl,
        string? Description,
        IReadOnlyDictionary<string, string>? Metadata);

    private readonly record struct HlsSegmentSpec(Uri Uri, long? RangeOffset, long? RangeLength, double DurationSeconds);

    private const string DefaultProgressUserId = "local";
    private const string NativeWasmLibraryModeEnvVar = "EMMA_NATIVE_WASM_LIBRARY_MODE";
    private const string EmbeddedHandshakeOnStartupEnvVar = "EMMA_PLUGINHOST_HANDSHAKE_ON_STARTUP";
    private const string PluginHostHandshakeOnStartupEnvVar = "PluginHost__HandshakeOnStartup";
    private const string RequireSignedPluginsEnvVar = "EMMA_REQUIRE_SIGNED_PLUGINS";
    private const string PluginSignatureRequireSignedEnvVar = "PluginSignature__RequireSignedPlugins";
    private const string PluginSignatureDelegationDirectoryEnvVar = "EMMA_PLUGIN_SIGNATURE_DELEGATION_DIR";
    private const string PluginSignatureDelegationDirectoryConfigEnvVar = "PluginSignature__DelegationDirectory";
    private const string PluginSignatureRootKeyDirectoryEnvVar = "EMMA_PLUGIN_SIGNATURE_ROOT_KEY_DIR";
    private const string PluginSignatureRootKeyDirectoryConfigEnvVar = "PluginSignature__RootKeyDirectory";
    private const string DevModeEnvVar = "EMMA_PLUGIN_DEV_MODE";
    private const string PluginHostConsoleLogsEnvVar = "EMMA_PLUGINHOST_CONSOLE_LOGS";
    private const string PluginHostLogLevelEnvVar = "EMMA_PLUGINHOST_LOG_LEVEL";
    private const string DownloadMaxConcurrentEnvVar = "EMMA_DOWNLOAD_MAX_CONCURRENT";
    private const string DownloadMaxConcurrentConfigEnvVar = "Download__MaxConcurrent";
    private const string HostAuthHeader = "x-emma-plugin-host-auth";
    private static ServiceProvider? _serviceProvider;
    private static PluginRegistry? _registry;
    private static PluginHandshakeService? _handshake;
    private static PluginResolutionService? _pluginResolution;
    private static IWasmPluginRuntimeHost? _wasmRuntime;
    private static DownloadOrchestrator? _downloadOrchestrator;
    private static readonly DownloadExecutionOptions _downloadExecutionOptions = new()
    {
        MaxConcurrentDownloads = ResolveMaxConcurrentDownloads()
    };
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
    /// Initializes the embedded plugin host using the supplied manifest and sandbox directories.
    /// </summary>
    /// <param name="manifestsDir">The directory that contains plugin manifest files.</param>
    /// <param name="sandboxDir">The directory used for plugin sandbox state and extracted assets.</param>
    /// <returns><c>0</c> when initialization succeeds; otherwise <c>-1</c>.</returns>
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

                        var delegationDirectory = ResolvePluginSignatureDelegationDirectory();
                        if (!string.IsNullOrWhiteSpace(delegationDirectory))
                        {
                            typeof(PluginSignatureOptions).GetProperty(nameof(PluginSignatureOptions.DelegationDirectory))!
                                .SetValue(options, delegationDirectory);
                        }

                        var rootKeyDirectory = ResolvePluginSignatureRootKeyDirectory();
                        if (!string.IsNullOrWhiteSpace(rootKeyDirectory))
                        {
                            typeof(PluginSignatureOptions).GetProperty(nameof(PluginSignatureOptions.RootKeyDirectory))!
                                .SetValue(options, rootKeyDirectory);
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
                services.AddSingleton<PluginHostMetrics>();
                services.AddSingleton<IWasmComponentInvoker, NativeInProcessWasmComponentInvoker>();
                services.AddSingleton<IWasmPluginRuntimeHost, WasmPluginRuntimeHost>();
                services.AddSingleton<IPluginSignatureVerifier, DelegatedPluginSignatureVerifier>();

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
                    _downloadExecutionOptions,
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

    private static string? ResolvePluginSignatureDelegationDirectory()
    {
        return Environment.GetEnvironmentVariable(PluginSignatureDelegationDirectoryEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureDelegationDirectoryConfigEnvVar);
    }

    private static string? ResolvePluginSignatureRootKeyDirectory()
    {
        return Environment.GetEnvironmentVariable(PluginSignatureRootKeyDirectoryEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureRootKeyDirectoryConfigEnvVar);
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

    private static int ResolveMaxConcurrentDownloads()
    {
        var value = Environment.GetEnvironmentVariable(DownloadMaxConcurrentEnvVar)
            ?? Environment.GetEnvironmentVariable(DownloadMaxConcurrentConfigEnvVar);

        if (!string.IsNullOrWhiteSpace(value)
            && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return DownloadExecutionOptions.ClampMaxConcurrentDownloads(parsed);
        }

        return DownloadExecutionOptions.DefaultMaxConcurrentDownloads;
    }

    private static string ResolvePluginBuildType(PluginManifest manifest)
    {
        if (_serviceProvider?.GetService<IPluginEntrypointResolver>() is { } entrypointResolver
            && entrypointResolver.TryResolveWasmComponent(manifest, out var componentPath))
        {
            return Path.GetExtension(componentPath).Equals(".cwasm", StringComparison.OrdinalIgnoreCase)
                ? "cwasm"
                : "wasm";
        }

        return "csharp";
    }

    /// <summary>
    /// Shuts down the embedded plugin host and releases cached runtime resources.
    /// </summary>
    public static void ShutdownManaged()
    {
        lock (_initLock)
        {
            if (!_initialized)
            {
                return;
            }

            try
            {
                _serviceProvider?
                    .GetService<PluginProcessManager>()?
                    .StopAllAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                SetLastError(ex);
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
    /// Lists all discovered plugins as a JSON payload.
    /// </summary>
    /// <returns>A JSON array of plugin summaries, or <see langword="null"/> when the operation fails.</returns>
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
                BuildType: ResolvePluginBuildType(r.Manifest),
                ThumbnailAspectRatio: ResolveThumbnailAspectRatio(r.Manifest),
                ThumbnailFit: r.Manifest.Thumbnail?.Fit,
                ThumbnailWidth: r.Manifest.Thumbnail?.Width,
                ThumbnailHeight: r.Manifest.Thumbnail?.Height,
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
    /// Reloads plugin manifests and refreshes handshake state without recreating the host.
    /// </summary>
    /// <returns><c>0</c> when the rescan succeeds; otherwise <c>-1</c>.</returns>
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

    private static double? ResolveThumbnailAspectRatio(PluginManifest manifest)
    {
        var thumbnail = manifest.Thumbnail;
        if (thumbnail is null)
        {
            return null;
        }

        if (thumbnail.AspectRatio is { } aspect && aspect > 0)
        {
            return aspect;
        }

        if (thumbnail.Width is { } width
            && thumbnail.Height is { } height
            && width > 0
            && height > 0)
        {
            return (double)width / height;
        }

        return null;
    }

    /// <summary>
    /// Lists configured plugin repositories as JSON.
    /// </summary>
    /// <returns>A JSON array of repository records, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Adds a plugin repository to the local repository store.
    /// </summary>
    /// <param name="catalogUrl">The repository catalog URL.</param>
    /// <param name="repositoryId">An optional explicit repository identifier.</param>
    /// <param name="name">An optional display name for the repository.</param>
    /// <param name="sourceRepositoryUrl">An optional source code repository URL.</param>
    /// <returns><c>1</c> when the repository is added; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Removes a configured plugin repository.
    /// </summary>
    /// <param name="repositoryId">The repository identifier to remove.</param>
    /// <returns><c>1</c> when the repository is removed; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Lists the plugins published by a specific repository as JSON.
    /// </summary>
    /// <param name="repositoryId">The repository identifier to inspect.</param>
    /// <param name="refreshCatalog">When set, refreshes the repository catalog before listing plugins.</param>
    /// <returns>A JSON payload describing the repository plugins, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Lists plugins across all configured repositories as JSON.
    /// </summary>
    /// <param name="refreshCatalog">When set, refreshes repository catalogs before listing plugins.</param>
    /// <returns>A JSON array of repository plugin views, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Installs a plugin release from a configured repository.
    /// </summary>
    /// <param name="repositoryId">The repository identifier to install from.</param>
    /// <param name="pluginId">The plugin identifier to install.</param>
    /// <param name="version">An optional version to install; when omitted, the latest applicable release is used.</param>
    /// <param name="refreshCatalog">When set, refreshes the repository catalog before installation.</param>
    /// <param name="rescanAfterInstall">When set, rescans plugin manifests after installation completes.</param>
    /// <returns>A JSON installation result payload, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Enqueues a media download job and returns the created job as JSON.
    /// </summary>
    /// <param name="pluginId">The plugin that can resolve the media asset.</param>
    /// <param name="mediaId">The media identifier to download.</param>
    /// <param name="mediaType">The media type associated with the download.</param>
    /// <param name="chapterId">An optional chapter identifier for paged media downloads.</param>
    /// <param name="streamId">An optional stream identifier for video downloads.</param>
    /// <returns>A JSON download job payload, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Lists download jobs as JSON.
    /// </summary>
    /// <param name="limit">The maximum number of jobs to return.</param>
    /// <returns>A JSON array of download jobs, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Gets the configured maximum number of concurrent downloads.
    /// </summary>
    /// <returns>The current maximum concurrent download count.</returns>
    public static int GetMaxConcurrentDownloadsManaged()
    {
        return _downloadExecutionOptions.MaxConcurrentDownloads;
    }

    /// <summary>
    /// Updates the configured maximum number of concurrent downloads.
    /// </summary>
    /// <param name="maxConcurrentDownloads">The requested maximum concurrent download count.</param>
    /// <returns><see langword="true"/> when the update succeeds; otherwise <see langword="false"/>.</returns>
    public static bool SetMaxConcurrentDownloadsManaged(int maxConcurrentDownloads)
    {
        ClearLastError();

        try
        {
            _downloadExecutionOptions.MaxConcurrentDownloads = maxConcurrentDownloads;
            _downloadOrchestrator?.NotifyCapacityChanged();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return false;
        }
    }

    /// <summary>
    /// Gets a single download job as JSON.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <returns>A JSON download job payload, or <see langword="null"/> when the job is missing or the operation fails.</returns>
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

    /// <summary>
    /// Pauses a download job.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <returns><c>1</c> when the job is updated; otherwise <c>0</c>.</returns>
    public static int PauseDownloadManaged(string jobId)
    {
        return ChangeDownloadStateManaged(jobId, static (orchestrator, id) => orchestrator.PauseAsync(id, CancellationToken.None));
    }

    /// <summary>
    /// Resumes a paused download job.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <returns><c>1</c> when the job is updated; otherwise <c>0</c>.</returns>
    public static int ResumeDownloadManaged(string jobId)
    {
        return ChangeDownloadStateManaged(jobId, static (orchestrator, id) => orchestrator.ResumeAsync(id, CancellationToken.None));
    }

    /// <summary>
    /// Retries a failed download job.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <returns><c>1</c> when the job is updated; otherwise <c>0</c>.</returns>
    public static int RetryDownloadManaged(string jobId)
    {
        return ChangeDownloadStateManaged(jobId, static (orchestrator, id) => orchestrator.RetryAsync(id, CancellationToken.None));
    }

    /// <summary>
    /// Cancels a download job.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <returns><c>1</c> when the job is updated; otherwise <c>0</c>.</returns>
    public static int CancelDownloadManaged(string jobId)
    {
        return ChangeDownloadStateManaged(jobId, static (orchestrator, id) => orchestrator.CancelAsync(id, CancellationToken.None));
    }

    /// <summary>
    /// Deletes a download job.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <returns><c>1</c> when the job is deleted; otherwise <c>0</c>.</returns>
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
    /// Searches a plugin and returns the results as JSON.
    /// </summary>
    /// <param name="pluginId">The plugin identifier to query.</param>
    /// <param name="query">The search query to execute.</param>
    /// <returns>A JSON array of media summaries, or <see langword="null"/> when the operation fails.</returns>
    public static string? SearchJsonManaged(string pluginId, string query)
    {
        return SearchJsonManaged(pluginId, query, null);
    }

    /// <summary>
    /// Resolves a plugin and verifies that it is available for use.
    /// </summary>
    /// <param name="pluginId">The plugin identifier to resolve.</param>
    /// <returns><c>1</c> when the plugin resolves successfully; otherwise <c>0</c>.</returns>
    public static int OpenPluginManaged(string pluginId)
    {
        ClearLastError();

        try
        {
            return TryResolvePlugin(pluginId, out _, out _) ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    /// <summary>
    /// Searches a plugin and returns the results as JSON using an optional correlation identifier.
    /// </summary>
    /// <param name="pluginId">The plugin identifier to query.</param>
    /// <param name="query">The search query to execute.</param>
    /// <param name="correlationId">An optional correlation identifier forwarded to the plugin runtime.</param>
    /// <returns>A JSON array of media summaries, or <see langword="null"/> when the operation fails.</returns>
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
                var results = _wasmRuntime.SearchAsync(record, normalizedQuery, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                json = JsonSerializer.Serialize(results, PluginHostExportsJsonContext.Default.IReadOnlyListMediaSummary);
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

    /// <summary>
    /// Runs the WASM benchmark path for a plugin.
    /// </summary>
    /// <param name="pluginId">The WASM plugin identifier to benchmark.</param>
    /// <param name="iterations">The requested benchmark iteration count.</param>
    /// <returns>A JSON benchmark payload, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Enriches a selected media item with additional metadata for the active plugin.
    /// </summary>
    /// <param name="pluginId">The plugin identifier that owns the media item.</param>
    /// <param name="mediaJson">The selected media summary serialized as JSON.</param>
    /// <param name="correlationId">An optional correlation identifier for the enrichment request.</param>
    /// <returns>An enriched media summary as JSON, or <see langword="null"/> when the operation fails.</returns>
    public static string? EnrichMediaJsonManaged(string pluginId, string mediaJson, string? correlationId = null)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            if (!TryResolvePlugin(pluginId, out var record, out var address))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(mediaJson))
            {
                SetLastError("Media payload is required.");
                return null;
            }

            using var doc = JsonDocument.Parse(mediaJson);
            var root = doc.RootElement;
            var mediaId = ReadJsonString(root, "id") ?? ReadJsonString(root, "mediaId");
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("Invalid media payload: id is required.");
                return null;
            }

            var sourceId = ReadJsonString(root, "sourceId") ?? ReadJsonString(root, "source") ?? string.Empty;
            var title = ReadJsonString(root, "title") ?? string.Empty;
            var mediaType = ReadJsonString(root, "mediaType") ?? string.Empty;
            var thumbnailUrl = ReadJsonString(root, "thumbnailUrl") ?? ReadJsonString(root, "thumbnail_url");
            var description = ReadJsonString(root, "description");
            var metadata = ReadJsonObjectAsDictionary(root, "metadata") ?? ReadJsonObjectAsDictionary(root, "attributes");

            var media = new MediaSummary(
                MediaId.Create(mediaId),
                sourceId,
                title,
                ParseMediaType(mediaType),
                thumbnailUrl,
                description,
                metadata);

            if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
            {
                var enriched = _wasmRuntime
                    .EnrichSearchMetadataAsync(record, [media.Id.Value], [media], CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                var selected = enriched.FirstOrDefault() ?? media;
                return JsonSerializer.Serialize(
                    new EnrichedMediaSummaryResponse(
                        selected.Id.Value,
                        selected.SourceId,
                        selected.SourceId,
                        selected.Title,
                        selected.MediaType.ToString().ToLowerInvariant(),
                        selected.ThumbnailUrl,
                        selected.Description,
                        selected.Metadata),
                    PluginHostExportsJsonContext.Default.EnrichedMediaSummaryResponse);
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

            var enrichedMedia = EnrichGrpcPluginSearchMedia(record.Manifest.Id, address, media);
            if (enrichedMedia is not null)
            {
                return JsonSerializer.Serialize(
                    new EnrichedMediaSummaryResponse(
                        enrichedMedia.Id.Value,
                        enrichedMedia.SourceId,
                        enrichedMedia.SourceId,
                        enrichedMedia.Title,
                        enrichedMedia.MediaType.ToString().ToLowerInvariant(),
                        enrichedMedia.ThumbnailUrl,
                        enrichedMedia.Description,
                        enrichedMedia.Metadata),
                    PluginHostExportsJsonContext.Default.EnrichedMediaSummaryResponse);
            }

            return JsonSerializer.Serialize(
                new EnrichedMediaSummaryResponse(
                    media.Id.Value,
                    media.SourceId,
                    media.SourceId,
                    media.Title,
                    media.MediaType.ToString().ToLowerInvariant(),
                    media.ThumbnailUrl,
                    media.Description,
                    media.Metadata),
                PluginHostExportsJsonContext.Default.EnrichedMediaSummaryResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    /// <summary>
    /// Resolves suggestions for a lookup-backed search control.
    /// </summary>
    /// <param name="pluginId">The plugin identifier that owns the search control.</param>
    /// <param name="requestJson">The serialized suggestion request.</param>
    /// <param name="correlationId">An optional correlation identifier for the request.</param>
    /// <returns>A JSON array of suggestions, or <see langword="null"/> when the operation fails.</returns>
    public static string? SearchSuggestionsJsonManaged(string pluginId, string requestJson, string? correlationId = null)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            if (!TryResolvePlugin(pluginId, out var record, out var address))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(requestJson))
            {
                SetLastError("Search suggestion payload is required.");
                return null;
            }

            var request = JsonSerializer.Deserialize(
                requestJson,
                PluginHostExportsJsonContext.Default.SearchSuggestionRequest);
            if (request is null)
            {
                SetLastError("Invalid search suggestion payload.");
                return null;
            }

            var suggestions = _wasmRuntime!.IsWasmPlugin(record!.Manifest)
                ? _wasmRuntime.GetSearchSuggestionsAsync(record, request, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()
                : SearchGrpcPluginSuggestions(
                    record.Manifest.Id,
                    address,
                    request,
                    correlationId);

            return JsonSerializer.Serialize(
                suggestions,
                PluginHostExportsJsonContext.Default.IReadOnlyListSearchSuggestionItem);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    /// <summary>
    /// Runs the WASM network benchmark path for a plugin.
    /// </summary>
    /// <param name="pluginId">The WASM plugin identifier to benchmark.</param>
    /// <param name="query">The query payload used by the benchmark.</param>
    /// <returns>A JSON benchmark payload, or <see langword="null"/> when the operation fails.</returns>
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
    /// Searches a plugin and returns typed media results.
    /// </summary>
    /// <param name="pluginId">The plugin identifier to query.</param>
    /// <param name="query">The search query to execute.</param>
    /// <returns>The matching media summaries, or <see langword="null"/> when the operation fails.</returns>
    public static IReadOnlyList<MediaSummary>? SearchMediaManaged(string pluginId, string query)
    {
        return SearchMediaManaged(pluginId, query, null);
    }

    /// <summary>
    /// Searches a plugin and returns typed media results using an optional correlation identifier.
    /// </summary>
    /// <param name="pluginId">The plugin identifier to query.</param>
    /// <param name="query">The search query to execute.</param>
    /// <param name="correlationId">An optional correlation identifier forwarded to the plugin runtime.</param>
    /// <returns>The matching media summaries, or <see langword="null"/> when the operation fails.</returns>
    public static IReadOnlyList<MediaSummary>? SearchMediaManaged(string pluginId, string query, string? correlationId)
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

            var rawQuery = query ?? string.Empty;
            var parsedQuery = PluginSearchQuery.Parse(rawQuery, fallbackQuery: rawQuery);
            var normalizedQuery = parsedQuery.Query;
            var isStructuredQuery = LooksLikeJson(rawQuery)
                && (parsedQuery.Filters.Count > 0
                    || parsedQuery.QueryAdditions.Count > 0
                    || parsedQuery.MediaTypes.Count > 0
                    || !string.IsNullOrWhiteSpace(parsedQuery.Sort)
                    || parsedQuery.Page.HasValue
                    || parsedQuery.PageSize.HasValue);

            var runtimeSearchStopwatch = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<MediaSummary> results;
            if (_wasmRuntime!.IsWasmPlugin(record!.Manifest))
            {
                var wasmQuery = isStructuredQuery ? rawQuery : normalizedQuery;
                results = _wasmRuntime.SearchAsync(record, wasmQuery, CancellationToken.None)
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
                var grpcQuery = isStructuredQuery ? rawQuery : normalizedQuery;
                var request = new PluginContracts.SearchRequest
                {
                    Query = grpcQuery,
                    Context = new PluginContracts.RequestContext
                    {
                        CorrelationId = resolvedCorrelationId,
                        DeadlineUtc = deadlineUtc.ToString("O")
                    }
                };

                if (parsedQuery.MediaTypes.Count > 0)
                {
                    request.MediaTypes.AddRange(parsedQuery.MediaTypes);
                }

                foreach (var filter in parsedQuery.Filters)
                {
                    var grpcFilter = new PluginContracts.SearchFilter
                    {
                        Id = filter.Id,
                        Operation = filter.Operation ?? string.Empty
                    };
                    grpcFilter.Values.AddRange(filter.Values);
                    request.Filters.Add(grpcFilter);
                }

                foreach (var addition in parsedQuery.QueryAdditions)
                {
                    request.QueryAdditions.Add(new PluginContracts.SearchQueryAddition
                    {
                        Id = addition.Id,
                        Value = addition.Value,
                        Type = addition.Type ?? string.Empty
                    });
                }

                if (!string.IsNullOrWhiteSpace(parsedQuery.Sort))
                {
                    request.Sort = parsedQuery.Sort;
                }

                if (parsedQuery.Page is int page && page >= 0)
                {
                    request.Page = page;
                }

                if (parsedQuery.PageSize is int pageSize && pageSize > 0)
                {
                    request.PageSize = pageSize;
                }

                var response = client.SearchAsync(request, headers: headers, cancellationToken: CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                results = [.. response.Results.Select(MapPluginSearchSummary)];
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

    private static bool LooksLikeJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
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

    /// <summary>
    /// Returns and clears the last captured search timing payload.
    /// </summary>
    /// <returns>The last timing payload, or <see langword="null"/> when no timing is cached.</returns>
    public static string? TakeLastSearchTimingManaged()
    {
        lock (_searchTimingLock)
        {
            var value = _lastSearchTiming;
            _lastSearchTiming = null;
            return value;
        }
    }

    /// <summary>
    /// Loads chapters for a media item and returns them as JSON.
    /// </summary>
    /// <param name="pluginId">The plugin identifier that owns the media item.</param>
    /// <param name="mediaId">The media identifier to resolve chapters for.</param>
    /// <returns>A JSON array of chapters, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Loads a single page for a chapter and returns it as JSON.
    /// </summary>
    /// <param name="pluginId">The plugin identifier that owns the media item.</param>
    /// <param name="mediaId">The media identifier that owns the chapter.</param>
    /// <param name="chapterId">The chapter identifier that owns the page.</param>
    /// <param name="pageIndex">The zero-based page index to load.</param>
    /// <returns>A JSON page payload, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Loads a range of pages for a chapter and returns them as JSON.
    /// </summary>
    /// <param name="pluginId">The plugin identifier that owns the media item.</param>
    /// <param name="mediaId">The media identifier that owns the chapter.</param>
    /// <param name="chapterId">The chapter identifier to page through.</param>
    /// <param name="startIndex">The zero-based page index to start from.</param>
    /// <param name="count">The maximum number of pages to load.</param>
    /// <returns>A JSON page batch payload, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Resolves the asset payload for a single paged-media page and returns it as JSON.
    /// </summary>
    /// <param name="pluginId">The plugin identifier that owns the media item.</param>
    /// <param name="mediaId">The media identifier that owns the chapter.</param>
    /// <param name="chapterId">The chapter identifier that owns the page.</param>
    /// <param name="pageIndex">The zero-based page index to resolve.</param>
    /// <returns>A JSON page asset payload, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Loads video streams for a media item and returns them as JSON.
    /// </summary>
    /// <param name="pluginId">The plugin identifier that owns the media item.</param>
    /// <param name="mediaId">The media identifier to resolve streams for.</param>
    /// <returns>A JSON stream payload, or <see langword="null"/> when the operation fails.</returns>
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
        if (IsVideoLikeMediaType(normalizedType))
        {
            return await ExecuteVideoDownloadJobAsync(job, progress, cancellationToken);
        }

        return await ExecutePagedDownloadJobAsync(job, progress, cancellationToken);
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

    private static async Task<DownloadExecutionResult> ExecuteVideoDownloadJobAsync(
        DownloadJobRecord job,
        IProgress<DownloadExecutionProgress> progress,
        CancellationToken cancellationToken)
    {
        var streams = GetRemoteVideoStreamsManagedInternal(job.PluginId, job.MediaId);
        if (streams is null || streams.Count == 0)
        {
            return new DownloadExecutionResult(
                false,
                0,
                0,
                0,
                GetLastErrorManaged() ?? "No video streams available for download.");
        }

        var selectedStream = !string.IsNullOrWhiteSpace(job.StreamId)
            ? streams.FirstOrDefault(stream => string.Equals(stream.Id, job.StreamId, StringComparison.Ordinal))
            : streams.First();
        if (selectedStream is null)
        {
            return new DownloadExecutionResult(false, 0, 0, 0, "Requested stream was not found.");
        }

        if (selectedStream.DrmProtected)
        {
            return new DownloadExecutionResult(
                false,
                0,
                0,
                0,
                string.IsNullOrWhiteSpace(selectedStream.DrmScheme)
                    ? "DRM-protected streams are not supported for offline download."
                    : $"DRM-protected streams ({selectedStream.DrmScheme}) are not supported for offline download.");
        }

        if (selectedStream.IsLive)
        {
            return new DownloadExecutionResult(
                false,
                0,
                0,
                0,
                "Live streams are not supported for offline download.");
        }

        var streamRoot = BuildDownloadedVideoStreamRootPath(job.PluginId, job.MediaId, selectedStream.Id);
        var stagingStreamRoot = BuildVideoDownloadStagingStreamRootPath(job.PluginId, job.MediaId, selectedStream.Id);
        PrepareVideoDownloadStagingDirectory(stagingStreamRoot);
        var streamType = (selectedStream.StreamType ?? string.Empty).Trim().ToLowerInvariant();
        var explicitDash = streamType is "dash" or "mpd";
        var explicitHls = streamType is "hls" or "m3u8";
        var explicitDirect = streamType is "direct" or "file";

        if ((explicitDirect || string.IsNullOrWhiteSpace(streamType))
            && TryResolveDirectVideoUri(selectedStream.PlaylistUri, out var directVideoUri, out var directVideoExtension))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var totalDirect = 1;

            try
            {
                byte[] payload;
                if (directVideoUri.IsFile)
                {
                    payload = File.ReadAllBytes(directVideoUri.LocalPath);
                }
                else
                {
                    using var httpClient = CreateConfiguredVideoHttpClient(selectedStream);
                    payload = await DownloadBytesWithAuthAsync(httpClient, directVideoUri, cancellationToken);
                }

                if (payload.Length == 0)
                {
                    CleanupVideoDownloadStagingDirectory(stagingStreamRoot);
                    return new DownloadExecutionResult(
                        false,
                        0,
                        totalDirect,
                        0,
                        "Direct video stream returned an empty payload.");
                }

                var directPath = BuildDownloadedVideoDirectFilePathInDirectory(stagingStreamRoot, directVideoExtension);
                WriteDownloadPayload(directPath, payload);
                long directBytesDownloaded = payload.LongLength;
                var directCompleted = 1;
                PromoteVideoDownloadStagingDirectory(stagingStreamRoot, streamRoot);
                progress.Report(new DownloadExecutionProgress(directCompleted, totalDirect, directBytesDownloaded));
                return new DownloadExecutionResult(true, directCompleted, totalDirect, directBytesDownloaded, null);
            }
            catch (Exception ex)
            {
                CleanupVideoDownloadStagingDirectory(stagingStreamRoot);
                return new DownloadExecutionResult(
                    false,
                    0,
                    totalDirect,
                    0,
                    $"Failed to download direct video stream: {ex.Message}");
            }
        }

        string? hlsFailureMessage = null;
        if ((explicitDash || string.IsNullOrWhiteSpace(streamType))
            && TryResolveHttpDashManifestUri(selectedStream.PlaylistUri, out var dashManifestUri))
        {
            var dashResult = await DownloadHttpDashVideoStreamAsync(
                stagingStreamRoot,
                dashManifestUri,
                selectedStream,
                progress,
                cancellationToken);
            if (dashResult.Success)
            {
                PromoteVideoDownloadStagingDirectory(stagingStreamRoot, streamRoot);
                return dashResult;
            }

            hlsFailureMessage = dashResult.ErrorMessage;
        }

        if ((explicitHls || string.IsNullOrWhiteSpace(streamType))
            && TryResolveHttpHlsPlaylistUri(selectedStream.PlaylistUri, out var hlsPlaylistUri))
        {
            var hlsResult = await DownloadHttpHlsVideoStreamAsync(
                stagingStreamRoot,
                hlsPlaylistUri,
                selectedStream,
                progress,
                cancellationToken);
            if (hlsResult.Success)
            {
                PromoteVideoDownloadStagingDirectory(stagingStreamRoot, streamRoot);
                return hlsResult;
            }

            hlsFailureMessage = hlsResult.ErrorMessage;
        }

        if (explicitDash && !TryResolveHttpDashManifestUri(selectedStream.PlaylistUri, out _))
        {
            hlsFailureMessage = "Stream declares DASH but playlist URI is not a valid HTTP(S) MPD URL.";
        }

        if (explicitHls && !TryResolveHttpHlsPlaylistUri(selectedStream.PlaylistUri, out _))
        {
            hlsFailureMessage = "Stream declares HLS but playlist URI is not a valid HTTP(S) M3U8 URL.";
        }

        // Ensure HLS partial artifacts do not contaminate segment-mode fallback writes.
        PrepareVideoDownloadStagingDirectory(stagingStreamRoot);

        var completed = 0;
        var total = 0;
        long bytesDownloaded = 0;

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

                CleanupVideoDownloadStagingDirectory(stagingStreamRoot);
                return new DownloadExecutionResult(
                    false,
                    completed,
                    total,
                    bytesDownloaded,
                    error ?? "Failed to fetch video segment.");
            }

            var segmentPath = BuildDownloadedVideoSegmentPathInDirectory(stagingStreamRoot, sequence);

            WriteDownloadPayload(segmentPath, segment.Payload);
            bytesDownloaded += segment.Payload.LongLength;
            completed++;
            progress.Report(new DownloadExecutionProgress(completed, total, bytesDownloaded));
        }

        if (completed <= 0)
        {
            var error = GetLastErrorManaged();
            CleanupVideoDownloadStagingDirectory(stagingStreamRoot);
            return new DownloadExecutionResult(
                false,
                completed,
                total,
                bytesDownloaded,
                !string.IsNullOrWhiteSpace(hlsFailureMessage)
                    ? hlsFailureMessage
                    : string.IsNullOrWhiteSpace(error)
                    ? "No video segments were downloaded."
                    : error);
        }

        if (LooksLikeNonMediaSegments(stagingStreamRoot))
        {
            CleanupVideoDownloadStagingDirectory(stagingStreamRoot);
            return new DownloadExecutionResult(
                false,
                completed,
                total,
                bytesDownloaded,
                "Downloaded segments are not recognized as playable media payloads.");
        }

        PromoteVideoDownloadStagingDirectory(stagingStreamRoot, streamRoot);
        return new DownloadExecutionResult(true, completed, total, bytesDownloaded, null);
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
        return BuildDownloadedVideoSegmentPath(pluginId, mediaId, streamId, sequence, ".bin");
    }

    private static string BuildDownloadedVideoSegmentPath(string pluginId, string mediaId, string streamId, int sequence, string extension)
    {
        var normalizedExtension = NormalizeSegmentExtension(extension);
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "video",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId),
            SanitizePathSegment(streamId),
            $"{sequence:D6}{normalizedExtension}");
    }

    private static string BuildDownloadedVideoSegmentPathInDirectory(string streamDirectory, int sequence)
    {
        return BuildDownloadedVideoSegmentPathInDirectory(streamDirectory, sequence, ".ts");
    }

    private static string BuildDownloadedVideoSegmentPathInDirectory(string streamDirectory, int sequence, string extension)
    {
        var normalizedExtension = NormalizeSegmentExtension(extension);
        return Path.Combine(streamDirectory, $"{sequence:D6}{normalizedExtension}");
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

    private static string BuildDownloadedVideoDirectFilePathInDirectory(string streamDirectory, string extension)
    {
        var normalizedExtension = NormalizeDirectVideoExtension(extension);
        return Path.Combine(streamDirectory, $"source{normalizedExtension}");
    }

    private static string BuildDownloadedVideoStreamRootPath(string pluginId, string mediaId, string streamId)
    {
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "video",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId),
            SanitizePathSegment(streamId));
    }

    private static string BuildVideoDownloadStagingStreamRootPath(string pluginId, string mediaId, string streamId)
    {
        var suffix = Guid.NewGuid().ToString("n");
        return Path.Combine(
            ResolveDownloadRootDirectory(),
            "video",
            SanitizePathSegment(pluginId),
            SanitizePathSegment(mediaId),
            $"{SanitizePathSegment(streamId)}.__tmp_{suffix}");
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
            case "audio":
            case "music":
            case "podcast":
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

    private static void PrepareVideoDownloadStagingDirectory(string stagingDirectory)
    {
        DeleteDirectoryIfExists(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);
    }

    private static void CleanupVideoDownloadStagingDirectory(string stagingDirectory)
    {
        DeleteDirectoryIfExists(stagingDirectory);
    }

    private static void PromoteVideoDownloadStagingDirectory(string stagingDirectory, string finalDirectory)
    {
        if (!Directory.Exists(stagingDirectory))
        {
            throw new InvalidOperationException("Video download staging directory does not exist.");
        }

        DeleteDirectoryIfExists(finalDirectory);

        var parent = Path.GetDirectoryName(finalDirectory);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        Directory.Move(stagingDirectory, finalDirectory);
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
        var candidates = new[]
        {
            BuildDownloadedVideoSegmentPath(pluginId, mediaId, streamId, sequence, ".bin"),
            BuildDownloadedVideoSegmentPath(pluginId, mediaId, streamId, sequence, ".m4s"),
            BuildDownloadedVideoSegmentPath(pluginId, mediaId, streamId, sequence, ".ts"),
            BuildDownloadedVideoSegmentPath(pluginId, mediaId, streamId, sequence, ".mp4"),
            BuildDownloadedVideoSegmentPath(pluginId, mediaId, streamId, sequence, ".aac"),
            BuildDownloadedVideoSegmentPath(pluginId, mediaId, streamId, sequence, ".mp3")
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        asset = new VideoSegmentAssetResponse(
            "application/octet-stream",
            File.ReadAllBytes(path),
            File.GetLastWriteTimeUtc(path).ToString("O"));
        return true;
    }

    private static bool TryAssembleDashOfflineDirectMp4(string streamDirectory, string outputFilePath)
    {
        try
        {
            var initPath = Path.Combine(streamDirectory, "init.mp4");
            if (!File.Exists(initPath))
            {
                return false;
            }

            var orderedSegments = EnumerateDownloadedVideoSegments(streamDirectory)
                .Select(item => item.Path)
                .ToList();
            if (orderedSegments.Count == 0)
            {
                return false;
            }

            var outputDirectory = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            var initLength = new FileInfo(initPath).Length;
            using var output = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using (var initInput = new FileStream(initPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                initInput.CopyTo(output);
            }

            foreach (var segmentPath in orderedSegments)
            {
                using var segmentInput = new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                segmentInput.CopyTo(output);
            }

            output.Flush();
            return output.Length > initLength;
        }
        catch
        {
            try
            {
                if (File.Exists(outputFilePath))
                {
                    File.Delete(outputFilePath);
                }
            }
            catch
            {
                // Best-effort cleanup; keep the caller path non-throwing.
            }

            return false;
        }
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

            if (streamId.Contains(".__tmp_", StringComparison.Ordinal))
            {
                continue;
            }

            var playlistPath = BuildDownloadedVideoPlaylistPath(pluginId, mediaId, streamId);
            var videoPlaylistPath = Path.Combine(streamDirectory, "video.m3u8");
            var audioPlaylistPath = Path.Combine(streamDirectory, "audio.m3u8");
            if (File.Exists(playlistPath)
                && File.Exists(videoPlaylistPath)
                && File.Exists(audioPlaylistPath))
            {
                streams.Add(new VideoStreamResponse(
                    streamId,
                    $"Offline {streamId}",
                    new Uri(playlistPath).AbsoluteUri));
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

            if (!EnumerateDownloadedVideoSegments(streamDirectory).Any())
            {
                continue;
            }

            if (LooksLikeNonMediaSegments(streamDirectory))
            {
                continue;
            }

            var initSegmentPath = Path.Combine(streamDirectory, "init.mp4");
            var hasInitSegment = File.Exists(initSegmentPath);
            if (hasInitSegment)
            {
                var directDashPath = BuildDownloadedVideoDirectFilePathInDirectory(streamDirectory, ".mp4");
                if (!File.Exists(directDashPath))
                {
                    TryAssembleDashOfflineDirectMp4(streamDirectory, directDashPath);
                }

                if (File.Exists(directDashPath))
                {
                    streams.Add(new VideoStreamResponse(
                        streamId,
                        $"Offline {streamId}",
                        new Uri(directDashPath).AbsoluteUri));
                    continue;
                }
            }

            if (File.Exists(playlistPath))
            {
                if (hasInitSegment && !PlaylistContainsInitializationMap(playlistPath))
                {
                    if (!TryWriteOfflineVideoPlaylist(streamDirectory, playlistPath, initializationSegmentName: "init.mp4"))
                    {
                        continue;
                    }
                }
            }
            else if (!TryWriteOfflineVideoPlaylist(
                streamDirectory,
                playlistPath,
                initializationSegmentName: hasInitSegment ? "init.mp4" : null))
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

    private static IReadOnlyList<VideoStreamResponse>? GetRemoteVideoStreamsManagedInternal(string pluginId, string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            SetLastError("Media ID is required");
            return null;
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

            return [.. wasmStreams
                .Select(stream => new VideoStreamResponse(
                    stream.Id ?? string.Empty,
                    stream.Label ?? string.Empty,
                    stream.PlaylistUri ?? string.Empty,
                    stream.RequestHeaders,
                    stream.RequestCookies,
                    stream.StreamType,
                    stream.IsLive,
                    stream.DrmProtected,
                    stream.DrmScheme,
                    stream.AudioTracks?.Select(track => new VideoTrackResponse(
                        track.Id ?? string.Empty,
                        track.Label ?? string.Empty,
                        track.Language,
                        track.Codec,
                        track.IsDefault)).ToList(),
                    stream.SubtitleTracks?.Select(track => new VideoTrackResponse(
                        track.Id ?? string.Empty,
                        track.Label ?? string.Empty,
                        track.Language,
                        track.Codec,
                        track.IsDefault)).ToList(),
                    stream.DefaultAudioTrackId,
                    stream.DefaultSubtitleTrackId))];
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

        return [.. response.Streams
            .Select(stream => new VideoStreamResponse(
                stream.Id ?? string.Empty,
                stream.Label ?? string.Empty,
                stream.PlaylistUri ?? string.Empty,
                stream.RequestHeaders?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
                string.IsNullOrWhiteSpace(stream.RequestCookies) ? null : stream.RequestCookies,
                string.IsNullOrWhiteSpace(stream.StreamType) ? null : stream.StreamType,
                stream.IsLive,
                stream.DrmProtected,
                string.IsNullOrWhiteSpace(stream.DrmScheme) ? null : stream.DrmScheme,
                stream.AudioTracks.Select(track => new VideoTrackResponse(
                    track.Id ?? string.Empty,
                    track.Label ?? string.Empty,
                    string.IsNullOrWhiteSpace(track.Language) ? null : track.Language,
                    string.IsNullOrWhiteSpace(track.Codec) ? null : track.Codec,
                    track.IsDefault)).ToList(),
                stream.SubtitleTracks.Select(track => new VideoTrackResponse(
                    track.Id ?? string.Empty,
                    track.Label ?? string.Empty,
                    string.IsNullOrWhiteSpace(track.Language) ? null : track.Language,
                    string.IsNullOrWhiteSpace(track.Codec) ? null : track.Codec,
                    track.IsDefault)).ToList(),
                string.IsNullOrWhiteSpace(stream.DefaultAudioTrackId) ? null : stream.DefaultAudioTrackId,
                string.IsNullOrWhiteSpace(stream.DefaultSubtitleTrackId) ? null : stream.DefaultSubtitleTrackId))];
    }

    private static bool TryWriteOfflineVideoPlaylist(
        string streamDirectory,
        string playlistPath,
        IReadOnlyList<double>? segmentDurationsSeconds = null,
        string? initializationSegmentName = null)
    {
        var segments = EnumerateDownloadedVideoSegments(streamDirectory).ToList();

        if (segments.Count == 0)
        {
            return false;
        }

        var durations = new double[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            durations[i] = i < (segmentDurationsSeconds?.Count ?? 0)
                ? Math.Max(0.1, segmentDurationsSeconds![i])
                : 6.0;
        }

        var targetDuration = Math.Max(1, (int)Math.Ceiling(durations.Max()));
        var hlsVersion = string.IsNullOrWhiteSpace(initializationSegmentName) ? 3 : 7;

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine($"#EXT-X-VERSION:{hlsVersion}");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{targetDuration}");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        if (!string.IsNullOrWhiteSpace(initializationSegmentName))
        {
            sb.AppendLine($"#EXT-X-MAP:URI=\"{initializationSegmentName}\"");
        }
        for (var i = 0; i < segments.Count; i++)
        {
            sb.AppendLine($"#EXTINF:{durations[i].ToString("0.000", CultureInfo.InvariantCulture)},");
            sb.AppendLine(segments[i].Name);
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

    private static bool TryWriteOfflineVideoPlaylistWithSegments(
        string playlistPath,
        IReadOnlyList<string> segmentNames,
        IReadOnlyList<double> segmentDurationsSeconds,
        string? initializationSegmentName = null)
    {
        if (segmentNames.Count == 0 || segmentDurationsSeconds.Count == 0 || segmentNames.Count != segmentDurationsSeconds.Count)
        {
            return false;
        }

        var targetDuration = Math.Max(1, (int)Math.Ceiling(segmentDurationsSeconds.Max()));
        var hlsVersion = string.IsNullOrWhiteSpace(initializationSegmentName) ? 3 : 7;

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine($"#EXT-X-VERSION:{hlsVersion}");
        sb.AppendLine($"#EXT-X-TARGETDURATION:{targetDuration}");
        sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
        if (!string.IsNullOrWhiteSpace(initializationSegmentName))
        {
            sb.AppendLine($"#EXT-X-MAP:URI=\"{initializationSegmentName}\"");
        }

        for (var i = 0; i < segmentNames.Count; i++)
        {
            sb.AppendLine($"#EXTINF:{Math.Max(0.1, segmentDurationsSeconds[i]).ToString("0.000", CultureInfo.InvariantCulture)},");
            sb.AppendLine(segmentNames[i]);
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

    private static bool TryWriteDashMasterPlaylist(string playlistPath, string videoPlaylistName, string audioPlaylistName, long bandwidth)
    {
        if (string.IsNullOrWhiteSpace(videoPlaylistName) || string.IsNullOrWhiteSpace(audioPlaylistName))
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        sb.AppendLine("#EXT-X-VERSION:7");
        sb.AppendLine($"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"Default\",DEFAULT=YES,AUTOSELECT=YES,URI=\"{audioPlaylistName}\"");
        sb.AppendLine($"#EXT-X-STREAM-INF:BANDWIDTH={Math.Max(1, bandwidth)},AUDIO=\"audio\"");
        sb.AppendLine(videoPlaylistName);

        var parent = Path.GetDirectoryName(playlistPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(playlistPath, sb.ToString());
        return true;
    }

    private static bool IsDashVideoAdaptation(XElement adaptationSet)
    {
        var mimeType = (string?)adaptationSet.Attribute("mimeType") ?? string.Empty;
        var contentType = (string?)adaptationSet.Attribute("contentType") ?? string.Empty;
        if (mimeType.Contains("video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "video", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var representations = adaptationSet.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Representation", StringComparison.OrdinalIgnoreCase));
        return representations.Any(rep =>
            ((string?)rep.Attribute("mimeType") ?? string.Empty).Contains("video", StringComparison.OrdinalIgnoreCase)
            || ((string?)rep.Attribute("codecs") ?? string.Empty).Contains("avc", StringComparison.OrdinalIgnoreCase)
            || ((string?)rep.Attribute("codecs") ?? string.Empty).Contains("hvc", StringComparison.OrdinalIgnoreCase)
            || ((string?)rep.Attribute("codecs") ?? string.Empty).Contains("vp", StringComparison.OrdinalIgnoreCase)
            || ((string?)rep.Attribute("codecs") ?? string.Empty).Contains("av01", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDashAudioAdaptation(XElement adaptationSet)
    {
        var mimeType = (string?)adaptationSet.Attribute("mimeType") ?? string.Empty;
        var contentType = (string?)adaptationSet.Attribute("contentType") ?? string.Empty;
        if (mimeType.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "audio", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var representations = adaptationSet.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Representation", StringComparison.OrdinalIgnoreCase));
        return representations.Any(rep =>
            ((string?)rep.Attribute("mimeType") ?? string.Empty).Contains("audio", StringComparison.OrdinalIgnoreCase)
            || ((string?)rep.Attribute("codecs") ?? string.Empty).Contains("mp4a", StringComparison.OrdinalIgnoreCase)
            || ((string?)rep.Attribute("codecs") ?? string.Empty).Contains("opus", StringComparison.OrdinalIgnoreCase)
            || ((string?)rep.Attribute("codecs") ?? string.Empty).Contains("vorbis", StringComparison.OrdinalIgnoreCase));
    }

    private static int ParseSequenceFromPath(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return int.TryParse(name, out var parsed) && parsed >= 0
            ? parsed
            : -1;
    }

    private static string NormalizeSegmentExtension(string extension)
    {
        return PluginVideoSegmentFileNaming.NormalizeSupportedSegmentExtension(extension);
    }

    private static bool IsSupportedSegmentExtension(string extension)
    {
        var normalized = NormalizeSegmentExtension(extension);
        return normalized is ".bin" or ".m4s" or ".ts" or ".mp4" or ".aac" or ".mp3";
    }

    private static IEnumerable<(string Path, string Name, int Sort)> EnumerateDownloadedVideoSegments(string streamDirectory)
    {
        return Directory.EnumerateFiles(streamDirectory)
            .Select(path =>
            {
                var extension = Path.GetExtension(path);
                var sort = ParseSequenceFromPath(path);
                return new
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    Sort = sort,
                    Supported = IsSupportedSegmentExtension(extension)
                };
            })
            .Where(item => item.Supported && item.Sort >= 0 && !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Sort)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => (item.Path, item.Name, item.Sort));
    }

    private static bool PlaylistContainsInitializationMap(string playlistPath)
    {
        try
        {
            foreach (var line in File.ReadLines(playlistPath))
            {
                if (line.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore malformed/unreadable playlists and allow rewrite path to proceed.
        }

        return false;
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

    private static bool TryResolveHttpHlsPlaylistUri(string playlistUri, out Uri playlist)
    {
        playlist = default!;

        if (string.IsNullOrWhiteSpace(playlistUri)
            || !Uri.TryCreate(playlistUri.Trim(), UriKind.Absolute, out var parsedUri))
        {
            return false;
        }

        var scheme = parsedUri.Scheme?.Trim().ToLowerInvariant() ?? string.Empty;
        if (scheme is not "http" and not "https")
        {
            return false;
        }

        var absolutePath = parsedUri.AbsolutePath ?? string.Empty;
        if (!absolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        playlist = parsedUri;
        return true;
    }

    private static bool TryResolveHttpDashManifestUri(string playlistUri, out Uri manifest)
    {
        manifest = default!;

        if (string.IsNullOrWhiteSpace(playlistUri)
            || !Uri.TryCreate(playlistUri.Trim(), UriKind.Absolute, out var parsedUri))
        {
            return false;
        }

        var scheme = parsedUri.Scheme?.Trim().ToLowerInvariant() ?? string.Empty;
        if (scheme is not "http" and not "https")
        {
            return false;
        }

        var absolutePath = parsedUri.AbsolutePath ?? string.Empty;
        if (!absolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        manifest = parsedUri;
        return true;
    }

    private static HttpClient CreateConfiguredVideoHttpClient(VideoStreamResponse stream)
    {
        var client = new HttpClient();
        if (stream.RequestHeaders is not null)
        {
            foreach (var header in stream.RequestHeaders)
            {
                var name = (header.Key ?? string.Empty).Trim();
                var value = (header.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (string.Equals(name, "host", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "content-length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                client.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            }
        }

        if (!string.IsNullOrWhiteSpace(stream.RequestCookies)
            && !client.DefaultRequestHeaders.Contains("Cookie"))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", stream.RequestCookies);
        }

        return client;
    }

    private static async Task<byte[]> DownloadBytesWithAuthAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static async Task<string> DownloadStringWithAuthAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task<DownloadExecutionResult> DownloadHttpHlsVideoStreamAsync(
        string stagingStreamRoot,
        Uri playlistUri,
        VideoStreamResponse stream,
        IProgress<DownloadExecutionProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = CreateConfiguredVideoHttpClient(stream);
            var rootPlaylistText = await DownloadStringWithAuthAsync(httpClient, playlistUri, cancellationToken);

            if (string.IsNullOrWhiteSpace(rootPlaylistText))
            {
                return new DownloadExecutionResult(
                    false,
                    0,
                    0,
                    0,
                    "HLS playlist response was empty.");
            }

            var mediaPlaylistUri = ResolveHlsMediaPlaylistUri(playlistUri, rootPlaylistText);
            var mediaPlaylistText = string.Equals(mediaPlaylistUri.AbsoluteUri, playlistUri.AbsoluteUri, StringComparison.Ordinal)
                ? rootPlaylistText
                : await DownloadStringWithAuthAsync(httpClient, mediaPlaylistUri, cancellationToken);

            var parsed = ParseHlsSegments(mediaPlaylistUri, mediaPlaylistText);
            if (!string.IsNullOrWhiteSpace(parsed.Error))
            {
                return new DownloadExecutionResult(
                    false,
                    0,
                    0,
                    0,
                    parsed.Error);
            }

            var segments = parsed.Segments;
            if (segments.Count == 0)
            {
                return new DownloadExecutionResult(
                    false,
                    0,
                    0,
                    0,
                    "HLS media playlist did not contain downloadable segments.");
            }

            PrepareVideoDownloadStagingDirectory(stagingStreamRoot);

            var total = segments.Count;
            var completed = 0;
            long bytesDownloaded = 0;
            var parallelism = GetVideoDownloadParallelism();
            using var throttler = new SemaphoreSlim(parallelism);
            var downloadTasks = Enumerable.Range(0, segments.Count)
                .Select(async i =>
                {
                    await throttler.WaitAsync(cancellationToken);
                    try
                    {
                        var segmentExtension = PluginVideoSegmentFileNaming.ResolveSegmentExtension(segments[i].Uri, ".ts");
                        var segmentPath = BuildDownloadedVideoSegmentPathInDirectory(stagingStreamRoot, i, segmentExtension);
                        var bytesWritten = await DownloadHlsSegmentToPathAsync(httpClient, segments[i], segmentPath, cancellationToken);
                        if (bytesWritten <= 0)
                        {
                            throw new InvalidOperationException($"HLS segment {i} returned an empty payload.");
                        }

                        var currentBytes = Interlocked.Add(ref bytesDownloaded, bytesWritten);
                        var currentCompleted = Interlocked.Increment(ref completed);
                        progress.Report(new DownloadExecutionProgress(currentCompleted, total, currentBytes));
                    }
                    finally
                    {
                        throttler.Release();
                    }
                })
                .ToArray();
            await Task.WhenAll(downloadTasks);

            var playlistPath = Path.Combine(stagingStreamRoot, "offline.m3u8");
            if (!TryWriteOfflineVideoPlaylist(
                stagingStreamRoot,
                playlistPath,
                segments.Select(item => item.DurationSeconds).ToList()))
            {
                return new DownloadExecutionResult(
                    false,
                    completed,
                    total,
                    bytesDownloaded,
                    "Failed to generate offline playlist for downloaded HLS segments.");
            }

            return new DownloadExecutionResult(true, completed, total, bytesDownloaded, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadExecutionResult(
                false,
                0,
                0,
                0,
                $"Failed to download HLS stream: {ex.Message}");
        }
    }

    private static async Task<DownloadExecutionResult> DownloadHttpDashVideoStreamAsync(
        string stagingStreamRoot,
        Uri manifestUri,
        VideoStreamResponse stream,
        IProgress<DownloadExecutionProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = CreateConfiguredVideoHttpClient(stream);
            var mpdText = await DownloadStringWithAuthAsync(httpClient, manifestUri, cancellationToken);
            if (string.IsNullOrWhiteSpace(mpdText))
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DASH manifest response was empty.");
            }

            var doc = XDocument.Parse(mpdText, LoadOptions.PreserveWhitespace);
            var mpd = doc.Root;
            if (mpd is null || !string.Equals(mpd.Name.LocalName, "MPD", StringComparison.OrdinalIgnoreCase))
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DASH manifest is invalid.");
            }

            var mpdType = (string?)mpd.Attribute("type") ?? string.Empty;
            if (string.Equals(mpdType, "dynamic", StringComparison.OrdinalIgnoreCase))
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "Live DASH streams are not supported for offline download.");
            }

            if (mpd.Descendants().Any(element => string.Equals(element.Name.LocalName, "ContentProtection", StringComparison.OrdinalIgnoreCase)))
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DRM-protected DASH streams are not supported for offline download.");
            }

            var period = mpd.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "Period", StringComparison.OrdinalIgnoreCase));
            if (period is null)
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DASH manifest is missing a Period.");
            }

            var adaptationSets = period.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "AdaptationSet", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var videoAdaptation = adaptationSets.FirstOrDefault(IsDashVideoAdaptation)
                ?? adaptationSets.FirstOrDefault();
            if (videoAdaptation is null)
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DASH manifest did not contain a supported AdaptationSet.");
            }

            var audioAdaptation = adaptationSets.FirstOrDefault(IsDashAudioAdaptation);

            var representation = SelectDashRepresentation(videoAdaptation);
            if (representation is null)
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DASH representation was not found.");
            }

            var representationId = (string?)representation.Attribute("id") ?? "video";
            var segmentTemplate = representation.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "SegmentTemplate", StringComparison.OrdinalIgnoreCase))
                ?? videoAdaptation.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "SegmentTemplate", StringComparison.OrdinalIgnoreCase));
            if (segmentTemplate is null)
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DASH SegmentTemplate is required for offline download.");
            }

            var initTemplate = (string?)segmentTemplate.Attribute("initialization");
            var mediaTemplate = (string?)segmentTemplate.Attribute("media");
            if (string.IsNullOrWhiteSpace(initTemplate) || string.IsNullOrWhiteSpace(mediaTemplate))
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DASH SegmentTemplate is missing initialization or media pattern.");
            }

            var timescale = (long?)segmentTemplate.Attribute("timescale") ?? 1;
            if (timescale <= 0)
            {
                timescale = 1;
            }
            var maxSegments = GetVideoDownloadMaxSegments();
            var periodDurationSeconds = ParseIsoDurationSeconds((string?)period.Attribute("duration"))
                ?? ParseIsoDurationSeconds((string?)mpd.Attribute("mediaPresentationDuration"));

            var startNumber = (long?)segmentTemplate.Attribute("startNumber") ?? 1;
            if (startNumber <= 0)
            {
                startNumber = 1;
            }
            var bandwidth = (long?)representation.Attribute("bandwidth") ?? 0;

            var timeline = segmentTemplate.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "SegmentTimeline", StringComparison.OrdinalIgnoreCase));
            var segmentNumbers = new List<long>();
            var segmentTimes = new List<long>();
            var segmentDurations = new List<double>();
            if (timeline is not null)
            {
                var currentNumber = startNumber;
                var currentTime = 0L;
                var timelineEntries = timeline.Elements().Where(element => string.Equals(element.Name.LocalName, "S", StringComparison.OrdinalIgnoreCase)).ToList();
                for (var entryIndex = 0; entryIndex < timelineEntries.Count; entryIndex++)
                {
                    var entry = timelineEntries[entryIndex];
                    var d = (long?)entry.Attribute("d") ?? 0;
                    if (d <= 0)
                    {
                        continue;
                    }

                    var t = (long?)entry.Attribute("t");
                    if (t is not null)
                    {
                        currentTime = t.Value;
                    }

                    var r = (long?)entry.Attribute("r") ?? 0;
                    long repeats;
                    if (r >= 0)
                    {
                        repeats = r;
                    }
                    else
                    {
                        var nextT = entryIndex + 1 < timelineEntries.Count
                            ? (long?)timelineEntries[entryIndex + 1].Attribute("t")
                            : null;
                        repeats = ComputeDashNegativeRepeatCount(currentTime, d, nextT, periodDurationSeconds, timescale);
                    }

                    for (var i = 0; i <= repeats; i++)
                    {
                        segmentNumbers.Add(currentNumber++);
                        segmentTimes.Add(currentTime);
                        segmentDurations.Add((double)d / timescale);
                        currentTime += d;
                        if (segmentNumbers.Count >= maxSegments)
                        {
                            break;
                        }
                    }

                    if (segmentNumbers.Count >= maxSegments)
                    {
                        break;
                    }
                }
            }
            else
            {
                var durationUnits = (long?)segmentTemplate.Attribute("duration") ?? 0;
                if (durationUnits <= 0)
                {
                    return new DownloadExecutionResult(false, 0, 0, 0, "DASH SegmentTemplate duration or timeline is required.");
                }

                if (periodDurationSeconds is null || periodDurationSeconds.Value <= 0)
                {
                    return new DownloadExecutionResult(false, 0, 0, 0, "DASH duration could not be resolved for offline segment planning.");
                }

                var segmentSeconds = (double)durationUnits / timescale;
                var count = (int)Math.Ceiling(periodDurationSeconds.Value / segmentSeconds);
                count = Math.Clamp(count, 1, maxSegments);
                for (var i = 0; i < count; i++)
                {
                    segmentNumbers.Add(startNumber + i);
                    segmentTimes.Add(i * durationUnits);
                    segmentDurations.Add(segmentSeconds);
                }
            }

            if (segmentNumbers.Count == 0)
            {
                return new DownloadExecutionResult(false, 0, 0, 0, "DASH segment list was empty.");
            }

            PrepareVideoDownloadStagingDirectory(stagingStreamRoot);

            var manifestBase = new Uri(manifestUri, "./");
            var firstSegmentTime = segmentTimes.Count > 0 ? segmentTimes[0] : 0;
            var initUri = ResolveDashUri(manifestBase, representation, videoAdaptation, period, mpd, ExpandDashTemplate(initTemplate!, representationId, startNumber, firstSegmentTime, bandwidth));
            var initName = "init.mp4";
            var initPath = Path.Combine(stagingStreamRoot, initName);
            var initBytes = await DownloadToFileWithAuthAsync(httpClient, initUri, initPath, cancellationToken);
            if (initBytes <= 0)
            {
                return new DownloadExecutionResult(false, 0, segmentNumbers.Count + 1, 0, "DASH initialization segment was empty.");
            }

            var completed = 1;
            var total = segmentNumbers.Count + 1;
            long bytesDownloaded = initBytes;
            progress.Report(new DownloadExecutionProgress(completed, total, bytesDownloaded));
            var parallelism = GetVideoDownloadParallelism();
            using var throttler = new SemaphoreSlim(parallelism);
            var mediaDownloadTasks = Enumerable.Range(0, segmentNumbers.Count)
                .Select(async i =>
                {
                    await throttler.WaitAsync(cancellationToken);
                    try
                    {
                        var segmentNumber = segmentNumbers[i];
                        var segmentTime = segmentTimes[i];
                        var mediaPath = ExpandDashTemplate(mediaTemplate!, representationId, segmentNumber, segmentTime, bandwidth);
                        var mediaUri = ResolveDashUri(manifestBase, representation, videoAdaptation, period, mpd, mediaPath);
                        var segmentPath = BuildDownloadedVideoSegmentPathInDirectory(stagingStreamRoot, i, ".m4s");
                        var bytesWritten = await DownloadToFileWithAuthAsync(httpClient, mediaUri, segmentPath, cancellationToken);
                        if (bytesWritten <= 0)
                        {
                            throw new InvalidOperationException($"DASH media segment {segmentNumber} was empty.");
                        }

                        var currentBytes = Interlocked.Add(ref bytesDownloaded, bytesWritten);
                        var currentCompleted = Interlocked.Increment(ref completed);
                        progress.Report(new DownloadExecutionProgress(currentCompleted, total, currentBytes));
                    }
                    finally
                    {
                        throttler.Release();
                    }
                })
                .ToArray();
            await Task.WhenAll(mediaDownloadTasks);

            var hasAudioRendition = false;
            long audioBandwidth = 0;
            if (audioAdaptation is not null)
            {
                var audioRepresentation = SelectDashRepresentation(audioAdaptation);
                var audioSegmentTemplate = audioRepresentation?.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "SegmentTemplate", StringComparison.OrdinalIgnoreCase))
                    ?? audioAdaptation.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, "SegmentTemplate", StringComparison.OrdinalIgnoreCase));
                var audioInitTemplate = (string?)audioSegmentTemplate?.Attribute("initialization");
                var audioMediaTemplate = (string?)audioSegmentTemplate?.Attribute("media");
                if (audioRepresentation is not null
                    && audioSegmentTemplate is not null
                    && !string.IsNullOrWhiteSpace(audioInitTemplate)
                    && !string.IsNullOrWhiteSpace(audioMediaTemplate))
                {
                    var audioRepresentationId = (string?)audioRepresentation.Attribute("id") ?? "audio";
                    audioBandwidth = (long?)audioRepresentation.Attribute("bandwidth") ?? 0;
                    var audioStartNumber = (long?)audioSegmentTemplate.Attribute("startNumber") ?? 1;
                    if (audioStartNumber <= 0)
                    {
                        audioStartNumber = 1;
                    }

                    var audioInitUri = ResolveDashUri(
                        manifestBase,
                        audioRepresentation,
                        audioAdaptation,
                        period,
                        mpd,
                        ExpandDashTemplate(audioInitTemplate!, audioRepresentationId, audioStartNumber, firstSegmentTime, audioBandwidth));
                    var audioInitName = "audio_init.mp4";
                    var audioInitPath = Path.Combine(stagingStreamRoot, audioInitName);
                    var audioInitBytes = await DownloadToFileWithAuthAsync(httpClient, audioInitUri, audioInitPath, cancellationToken);
                    if (audioInitBytes <= 0)
                    {
                        return new DownloadExecutionResult(false, completed, total, bytesDownloaded, "DASH audio initialization segment was empty.");
                    }

                    total += segmentNumbers.Count + 1;
                    completed++;
                    bytesDownloaded += audioInitBytes;
                    progress.Report(new DownloadExecutionProgress(completed, total, bytesDownloaded));

                    var audioDownloadTasks = Enumerable.Range(0, segmentNumbers.Count)
                        .Select(async i =>
                        {
                            await throttler.WaitAsync(cancellationToken);
                            try
                            {
                                var segmentNumber = segmentNumbers[i];
                                var segmentTime = segmentTimes[i];
                                var mediaPath = ExpandDashTemplate(audioMediaTemplate!, audioRepresentationId, segmentNumber, segmentTime, audioBandwidth);
                                var mediaUri = ResolveDashUri(manifestBase, audioRepresentation, audioAdaptation, period, mpd, mediaPath);
                                var segmentPath = Path.Combine(stagingStreamRoot, $"audio_{i:D6}.m4s");
                                var bytesWritten = await DownloadToFileWithAuthAsync(httpClient, mediaUri, segmentPath, cancellationToken);
                                if (bytesWritten <= 0)
                                {
                                    throw new InvalidOperationException($"DASH audio segment {segmentNumber} was empty.");
                                }

                                var currentBytes = Interlocked.Add(ref bytesDownloaded, bytesWritten);
                                var currentCompleted = Interlocked.Increment(ref completed);
                                progress.Report(new DownloadExecutionProgress(currentCompleted, total, currentBytes));
                            }
                            finally
                            {
                                throttler.Release();
                            }
                        })
                        .ToArray();
                    await Task.WhenAll(audioDownloadTasks);
                    hasAudioRendition = true;
                }
            }

            // Build a direct fragmented MP4 fallback for runtimes that struggle with local fMP4 HLS playlists.
            var directDashPath = BuildDownloadedVideoDirectFilePathInDirectory(stagingStreamRoot, ".mp4");
            if (!TryAssembleDashOfflineDirectMp4(stagingStreamRoot, directDashPath))
            {
                return new DownloadExecutionResult(false, completed, total, bytesDownloaded, "Failed to assemble DASH offline direct MP4.");
            }

            var playlistPath = Path.Combine(stagingStreamRoot, "offline.m3u8");
            if (hasAudioRendition)
            {
                var videoPlaylistPath = Path.Combine(stagingStreamRoot, "video.m3u8");
                var audioPlaylistPath = Path.Combine(stagingStreamRoot, "audio.m3u8");
                var videoNames = Enumerable.Range(0, segmentNumbers.Count).Select(i => $"{i:D6}.m4s").ToList();
                var audioNames = Enumerable.Range(0, segmentNumbers.Count).Select(i => $"audio_{i:D6}.m4s").ToList();
                if (!TryWriteOfflineVideoPlaylistWithSegments(videoPlaylistPath, videoNames, segmentDurations, initName)
                    || !TryWriteOfflineVideoPlaylistWithSegments(audioPlaylistPath, audioNames, segmentDurations, "audio_init.mp4")
                    || !TryWriteDashMasterPlaylist(
                        playlistPath,
                        "video.m3u8",
                        "audio.m3u8",
                        Math.Max(1, bandwidth) + Math.Max(1, audioBandwidth)))
                {
                    return new DownloadExecutionResult(false, completed, total, bytesDownloaded, "Failed to generate offline playlists for DASH audio/video renditions.");
                }
            }
            else if (!TryWriteOfflineVideoPlaylist(stagingStreamRoot, playlistPath, segmentDurations, initName))
            {
                return new DownloadExecutionResult(false, completed, total, bytesDownloaded, "Failed to generate offline playlist for DASH segments.");
            }

            return new DownloadExecutionResult(true, completed, total, bytesDownloaded, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadExecutionResult(false, 0, 0, 0, $"Failed to download DASH stream: {ex.Message}");
        }
    }

    private static XElement? SelectDashRepresentation(XElement adaptationSet)
    {
        var representations = adaptationSet.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Representation", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (representations.Count == 0)
        {
            return null;
        }

        var ordered = representations
            .OrderBy(element => (int?)element.Attribute("bandwidth") ?? 0)
            .ToList();
        var withBandwidth = ordered
            .Where(element => ((int?)element.Attribute("bandwidth") ?? 0) > 0)
            .ToList();
        if (withBandwidth.Count == 0)
        {
            return ordered.Last();
        }

        var percentile = GetDashRepresentationPercentile();
        var targetIndex = (int)Math.Floor((withBandwidth.Count - 1) * percentile);
        targetIndex = Math.Clamp(targetIndex, 0, withBandwidth.Count - 1);
        return withBandwidth[targetIndex];
    }

    private static double GetDashRepresentationPercentile()
    {
        const string envName = "EMMA_VIDEO_DASH_BANDWIDTH_PERCENTILE";
        if (double.TryParse(Environment.GetEnvironmentVariable(envName), NumberStyles.Float, CultureInfo.InvariantCulture, out var configured)
            && configured >= 0d
            && configured <= 1d)
        {
            return configured;
        }

        // Default to a throughput-friendly upper-mid representation.
        return 0.60d;
    }

    private static int GetVideoDownloadParallelism()
    {
        const string envName = "EMMA_VIDEO_DOWNLOAD_MAX_PARALLEL";
        if (int.TryParse(Environment.GetEnvironmentVariable(envName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured))
        {
            return Math.Clamp(configured, 1, 32);
        }

        var suggested = Math.Max(4, Environment.ProcessorCount * 2);
        return Math.Clamp(suggested, 4, 16);
    }

    private static int GetVideoDownloadMaxSegments()
    {
        const string envName = "EMMA_VIDEO_DOWNLOAD_MAX_SEGMENTS";
        if (int.TryParse(Environment.GetEnvironmentVariable(envName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured))
        {
            return Math.Clamp(configured, 100, 20000);
        }

        return 5000;
    }

    private static long ComputeDashNegativeRepeatCount(
        long currentTime,
        long durationUnits,
        long? nextStartTime,
        double? periodDurationSeconds,
        long timescale)
    {
        if (durationUnits <= 0)
        {
            return 0;
        }

        if (nextStartTime is not null && nextStartTime.Value > currentTime)
        {
            return Math.Max(0, ((nextStartTime.Value - currentTime) / durationUnits) - 1);
        }

        if (periodDurationSeconds is not null && periodDurationSeconds.Value > 0)
        {
            var periodUnits = (long)Math.Floor(periodDurationSeconds.Value * timescale);
            if (periodUnits > currentTime)
            {
                return Math.Max(0, ((periodUnits - currentTime) / durationUnits) - 1);
            }
        }

        return 0;
    }

    private static string ExpandDashTemplate(string template, string representationId, long number, long time, long bandwidth)
    {
        var expanded = template
            .Replace("$RepresentationID$", representationId, StringComparison.Ordinal)
            .Replace("$Bandwidth$", bandwidth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$Number$", number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("$Time$", time.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        expanded = Regex.Replace(
            expanded,
            "\\$(Number|Time|Bandwidth)%0(\\d+)d\\$",
            match =>
            {
                var token = match.Groups[1].Value;
                var width = int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWidth)
                    ? Math.Clamp(parsedWidth, 1, 30)
                    : 1;
                var value = token switch
                {
                    "Number" => number,
                    "Time" => time,
                    "Bandwidth" => bandwidth,
                    _ => 0
                };
                return value.ToString($"D{width}", CultureInfo.InvariantCulture);
            },
            RegexOptions.CultureInvariant);

        return expanded;
    }

    private static Uri ResolveDashUri(Uri manifestBase, XElement representation, XElement adaptationSet, XElement period, XElement mpd, string relative)
    {
        var current = manifestBase;
        foreach (var element in new[] { mpd, period, adaptationSet, representation })
        {
            var baseUrl = element.Elements().FirstOrDefault(item => string.Equals(item.Name.LocalName, "BaseURL", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(baseUrl)
                && Uri.TryCreate(current, baseUrl, out var resolvedBase))
            {
                current = resolvedBase;
            }
        }

        return Uri.TryCreate(current, relative, out var resolved)
            ? resolved
            : new Uri(manifestBase, relative);
    }

    private static double? ParseIsoDurationSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return XmlConvert.ToTimeSpan(value.Trim()).TotalSeconds;
        }
        catch
        {
            return null;
        }
    }

    private static Uri ResolveHlsMediaPlaylistUri(Uri playlistUri, string playlistText)
    {
        var lines = playlistText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Uri? selectedVariant = null;
        var selectedBandwidth = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bandwidth = ParseBandwidthFromStreamInf(line);

            for (var j = i + 1; j < lines.Length; j++)
            {
                var candidate = lines[j].Trim();
                if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith('#'))
                {
                    continue;
                }

                if (!Uri.TryCreate(playlistUri, candidate, out var resolved))
                {
                    break;
                }

                if (bandwidth > selectedBandwidth)
                {
                    selectedBandwidth = bandwidth;
                    selectedVariant = resolved;
                }

                break;
            }
        }

        return selectedVariant ?? playlistUri;
    }

    private static int ParseBandwidthFromStreamInf(string streamInfLine)
    {
        const string key = "BANDWIDTH=";
        var index = streamInfLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }

        var start = index + key.Length;
        var end = start;
        while (end < streamInfLine.Length && char.IsDigit(streamInfLine[end]))
        {
            end++;
        }

        return int.TryParse(streamInfLine[start..end], out var parsed) && parsed > 0
            ? parsed
            : 0;
    }

    private static (List<HlsSegmentSpec> Segments, string? Error) ParseHlsSegments(Uri playlistUri, string playlistText)
    {
        var segments = new List<HlsSegmentSpec>();
        long? pendingByteRangeLength = null;
        long? pendingByteRangeOffset = null;
        double pendingDurationSeconds = 6.0;
        long nextImplicitByteRangeOffset = 0;
        var hasEndList = false;
        var lines = playlistText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
                {
                    hasEndList = true;
                }

                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[(line.IndexOf(':') + 1)..].Trim();
                    var commaIndex = value.IndexOf(',');
                    if (commaIndex >= 0)
                    {
                        value = value[..commaIndex];
                    }

                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration)
                        && parsedDuration > 0)
                    {
                        pendingDurationSeconds = parsedDuration;
                    }
                }

                if (line.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("METHOD=NONE", StringComparison.OrdinalIgnoreCase))
                {
                    return ([], "Encrypted HLS playlists are not currently supported for offline download.");
                }

                if (line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[(line.IndexOf(':') + 1)..].Trim();
                    if (!TryParseHlsByteRange(value, nextImplicitByteRangeOffset, out var length, out var offset))
                    {
                        return ([], "Invalid HLS EXT-X-BYTERANGE directive.");
                    }

                    pendingByteRangeLength = length;
                    pendingByteRangeOffset = offset;
                }

                continue;
            }

            if (Uri.TryCreate(playlistUri, line, out var segmentUri))
            {
                segments.Add(new HlsSegmentSpec(
                    segmentUri,
                    pendingByteRangeOffset,
                    pendingByteRangeLength,
                    pendingDurationSeconds));
                if (pendingByteRangeLength.HasValue)
                {
                    nextImplicitByteRangeOffset = (pendingByteRangeOffset ?? nextImplicitByteRangeOffset)
                        + pendingByteRangeLength.Value;
                }
                else
                {
                    nextImplicitByteRangeOffset = 0;
                }

                pendingByteRangeLength = null;
                pendingByteRangeOffset = null;
                pendingDurationSeconds = 6.0;
            }
        }

        if (!hasEndList)
        {
            return ([], "Live HLS playlists are not supported for offline download.");
        }

        return (segments, null);
    }

    private static bool TryParseHlsByteRange(
        string value,
        long implicitOffset,
        out long length,
        out long offset)
    {
        length = 0;
        offset = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('@', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!long.TryParse(parts[0], out length) || length <= 0)
        {
            return false;
        }

        if (parts.Length > 1)
        {
            if (!long.TryParse(parts[1], out offset) || offset < 0)
            {
                return false;
            }

            return true;
        }

        offset = implicitOffset;
        return offset >= 0;
    }

    private static async Task<byte[]> DownloadHlsSegmentPayloadAsync(HttpClient httpClient, HlsSegmentSpec segment, CancellationToken cancellationToken)
    {
        if (segment.RangeLength is null)
        {
            return await httpClient.GetByteArrayAsync(segment.Uri, cancellationToken);
        }

        var offset = segment.RangeOffset ?? 0;
        var length = segment.RangeLength.Value;
        var end = offset + length - 1;

        using var request = new HttpRequestMessage(HttpMethod.Get, segment.Uri);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, end);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            return payload;
        }

        // Some origins ignore Range and return full content with 200; slice requested window explicitly.
        if (offset >= payload.LongLength)
        {
            return [];
        }

        var available = payload.LongLength - offset;
        var take = (int)Math.Min(length, available);
        if (take <= 0)
        {
            return [];
        }

        var sliced = new byte[take];
        Buffer.BlockCopy(payload, (int)offset, sliced, 0, take);
        return sliced;
    }

    private static async Task<long> DownloadHlsSegmentToPathAsync(HttpClient httpClient, HlsSegmentSpec segment, string segmentPath, CancellationToken cancellationToken)
    {
        if (segment.RangeLength is null)
        {
            return await DownloadToFileWithAuthAsync(httpClient, segment.Uri, segmentPath, cancellationToken);
        }

        var payload = await DownloadHlsSegmentPayloadAsync(httpClient, segment, cancellationToken);
        if (payload.Length <= 0)
        {
            return 0;
        }

        WriteDownloadPayload(segmentPath, payload);
        return payload.LongLength;
    }

    private static async Task<long> DownloadToFileWithAuthAsync(HttpClient client, Uri uri, string destinationPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unexpected non-media response content type '{mediaType}' from {uri}.");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            return destination.Length;
        }
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

        if (candidates.Count == 0)
        {
            return null;
        }

        var preferredSource = candidates.FirstOrDefault(path =>
            Path.GetFileName(path).StartsWith("source.", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(preferredSource))
        {
            return preferredSource;
        }

        var nonInit = candidates.FirstOrDefault(path =>
            !string.Equals(Path.GetFileName(path), "init.mp4", StringComparison.OrdinalIgnoreCase));
        return nonInit ?? candidates[0];
    }

    private static bool LooksLikeNonMediaSegments(string streamDirectory)
    {
        var firstSegmentPath = EnumerateDownloadedVideoSegments(streamDirectory)
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

        // MPEG-TS packets start with 0x47. Fragmented MP4 often has an ftyp box at offset 4.
        if (bytes[0] == 0x47)
        {
            return false;
        }

        if (bytes.Length >= 8
            && bytes[4] == (byte)'f'
            && bytes[5] == (byte)'t'
            && bytes[6] == (byte)'y'
            && bytes[7] == (byte)'p')
        {
            return false;
        }

        // fMP4 media fragments often start with styp or moof boxes.
        if (bytes.Length >= 8
            && ((bytes[4] == (byte)'s' && bytes[5] == (byte)'t' && bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
                || (bytes[4] == (byte)'m' && bytes[5] == (byte)'o' && bytes[6] == (byte)'o' && bytes[7] == (byte)'f')))
        {
            return false;
        }

        if (bytes.Length >= 3
            && bytes[0] == (byte)'I'
            && bytes[1] == (byte)'D'
            && bytes[2] == (byte)'3')
        {
            return false;
        }

        var probeLength = Math.Min(bytes.Length, 128);
        var printableCount = 0;
        for (var i = 0; i < probeLength; i++)
        {
            var b = bytes[i];
            if (b == 9 || b == 10 || b == 13 || (b >= 32 && b <= 126))
            {
                printableCount++;
            }
        }

        // If the probe is almost entirely printable text, this is likely a placeholder payload.
        return printableCount >= (probeLength * 9) / 10;
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

    /// <summary>
    /// Loads a single video segment and returns it as JSON.
    /// </summary>
    /// <param name="pluginId">The plugin identifier that owns the media item.</param>
    /// <param name="mediaId">The media identifier that owns the stream.</param>
    /// <param name="streamId">The stream identifier that owns the segment.</param>
    /// <param name="sequence">The segment sequence number to load.</param>
    /// <returns>A JSON segment payload, or <see langword="null"/> when the operation fails.</returns>
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
    /// Gets the last host error message.
    /// </summary>
    /// <returns>The last error message, or <see langword="null"/> when no error is stored.</returns>
    public static string? GetLastErrorManaged()
    {
        lock (_errorLock)
        {
            return _lastError;
        }
    }

    /// <summary>
    /// Lists cached catalog media items as JSON.
    /// </summary>
    /// <param name="limit">The maximum number of catalog items to return.</param>
    /// <returns>A JSON array of media summaries, or <see langword="null"/> when the operation fails.</returns>
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
                .Select(item => new MediaSummary(
                    item.Id,
                    item.SourceId,
                    item.Title,
                    item.MediaType,
                    item.ThumbnailUrl,
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

    /// <summary>
    /// Lists media items stored in a library as JSON.
    /// </summary>
    /// <param name="userId">The library identifier or display name to read from.</param>
    /// <returns>A JSON array of media summaries, or <see langword="null"/> when the operation fails.</returns>
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

            var results = new List<MediaSummary>(entries.Count);
            foreach (var entry in entries)
            {
                var metadata = catalog.GetMediaAsync(entry.MediaId, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                if (metadata is null)
                {
                    results.Add(new MediaSummary(
                        entry.MediaId,
                        entry.SourceId,
                        entry.MediaId.Value,
                        MediaType.Paged));
                    continue;
                }

                var metadataDict = new Dictionary<string, string>();

                // Parse the metadata attributes JSON
                if (!string.IsNullOrWhiteSpace(metadata.Attributes))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(metadata.Attributes);
                        var root = doc.RootElement;
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            // Extract all metadata fields
                            foreach (var prop in root.EnumerateObject())
                            {
                                var value = prop.Value.ValueKind switch
                                {
                                    JsonValueKind.String => prop.Value.GetString(),
                                    JsonValueKind.Array =>
                                        string.Join(", ", prop.Value.EnumerateArray().Select(v => v.GetString())),
                                    _ => prop.Value.ToString(),
                                };
                                if (!string.IsNullOrEmpty(value))
                                {
                                    metadataDict[prop.Name] = value;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Not valid JSON, skip
                    }
                }

                results.Add(new MediaSummary(
                    metadata.Id,
                    metadata.SourceId,
                    metadata.Title,
                    metadata.MediaType,
                    metadata.ThumbnailUrl,
                    metadata.Synopsis,
                    metadataDict.Count > 0 ? metadataDict : null));
            }

            return JsonSerializer.Serialize(results, PluginHostExportsJsonContext.Default.IReadOnlyListMediaSummary);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    /// <summary>
    /// Refreshes library media metadata and chapter discovery state.
    /// </summary>
    /// <param name="libraryName">The library display name to refresh.</param>
    /// <returns>A JSON refresh summary payload, or <see langword="null"/> when the operation fails.</returns>
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
                var resolvedThumbnailUrl = metadata?.ThumbnailUrl;
                if (string.IsNullOrWhiteSpace(resolvedThumbnailUrl))
                {
                    resolvedThumbnailUrl = ResolveThumbnailUrlFromSearch(sourceId, mediaId, metadata?.Title ?? mediaId);
                }

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
                            var mediaTypeText = ToMediaTypeString(mediaType);
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
                        string.IsNullOrWhiteSpace(resolvedThumbnailUrl) ? null : resolvedThumbnailUrl,
                        null,
                        [],
                        now,
                        now)
                    : metadata with
                    {
                        UpdatedAtUtc = now,
                        SourceId = sourceId,
                        ThumbnailUrl = string.IsNullOrWhiteSpace(metadata.ThumbnailUrl)
                            ? (string.IsNullOrWhiteSpace(resolvedThumbnailUrl) ? null : resolvedThumbnailUrl)
                            : metadata.ThumbnailUrl
                    };

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

    /// <summary>
    /// Lists available libraries as JSON.
    /// </summary>
    /// <returns>A JSON array of library names, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Creates a new library.
    /// </summary>
    /// <param name="libraryName">The display name for the new library.</param>
    /// <returns><c>1</c> when the library is created; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Deletes an existing library.
    /// </summary>
    /// <param name="libraryName">The display name of the library to delete.</param>
    /// <returns><c>1</c> when the library is deleted; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Recreates the embedded host database and default library state.
    /// </summary>
    /// <returns><c>1</c> when the database is reset; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Checks whether a media item exists in one library or any library.
    /// </summary>
    /// <param name="mediaId">The media identifier to look up.</param>
    /// <param name="userId">The target library identifier, or <c>*</c> to search every library.</param>
    /// <returns><see langword="true"/> when the media item exists in the requested scope; otherwise <see langword="false"/>.</returns>
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

    /// <summary>
    /// Adds a media item to a library and upserts its catalog metadata.
    /// </summary>
    /// <param name="mediaId">The media identifier to add.</param>
    /// <param name="sourceId">The source or plugin identifier that owns the media item.</param>
    /// <param name="title">The media title to persist.</param>
    /// <param name="mediaType">The media type to persist.</param>
    /// <param name="userId">The target library identifier or display name.</param>
    /// <param name="description">An optional description string or JSON metadata payload.</param>
    /// <param name="thumbnailUrl">An optional thumbnail URL to persist.</param>
    /// <returns><c>1</c> when the media item is added; otherwise <c>0</c>.</returns>
    public static int AddMediaToLibraryManaged(
        string mediaId,
        string sourceId,
        string title,
        string mediaType,
        string userId = "Library",
        string? description = null,
        string? thumbnailUrl = null)
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

            var parsedMediaType = ParseMediaType(mediaType);
            var now = DateTimeOffset.UtcNow;
            var normalizedUserId = ToLibraryStorageKey(userId);

            // Parse combined JSON to extract description and metadata
            string? extractedDescription = null;
            var metadataJson = new Dictionary<string, object?>();

            if (!string.IsNullOrWhiteSpace(description))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(description);
                    var root = doc.RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        // Copy all fields to metadata, but extract description separately
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Name.Equals("description", StringComparison.OrdinalIgnoreCase))
                            {
                                extractedDescription = prop.Value.GetString();
                            }
                            else
                            {
                                metadataJson[prop.Name] = prop.Value.ValueKind switch
                                {
                                    System.Text.Json.JsonValueKind.String => prop.Value.GetString(),
                                    System.Text.Json.JsonValueKind.Array =>
                                        prop.Value.EnumerateArray().Select(v => v.GetString()).ToList(),
                                    _ => prop.Value.ToString(),
                                };
                            }
                        }
                    }
                }
                catch
                {
                    // If JSON parsing fails, treat as plain description
                    extractedDescription = description;
                }
            }

            var mediaCatalog = _serviceProvider!.GetRequiredService<IMediaCatalogPort>();
            // Store metadata as JSON in the Attributes field
            var attributesJsonString = metadataJson.Count > 0
                ? SerializeMetadataAttributes(metadataJson)
                : null;

            mediaCatalog.UpsertMediaAsync(
                new MediaMetadata(
                    MediaId.Create(mediaId),
                    sourceId ?? string.Empty,
                    title ?? string.Empty,
                    parsedMediaType,
                    null,
                    extractedDescription,
                    string.IsNullOrWhiteSpace(thumbnailUrl) ? null : thumbnailUrl,
                    null,
                    [],
                    now,
                    now,
                    attributesJsonString),
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

    /// <summary>
    /// Removes a media item from a library.
    /// </summary>
    /// <param name="mediaId">The media identifier to remove.</param>
    /// <param name="userId">The target library identifier or display name.</param>
    /// <returns><c>1</c> when the media item is removed; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Gets persisted reading or playback progress as JSON.
    /// </summary>
    /// <param name="mediaId">The media identifier to read progress for.</param>
    /// <param name="pluginId">The plugin identifier associated with the media item.</param>
    /// <param name="mediaType">The media type that determines which progress store is queried.</param>
    /// <param name="userId">The user or library scope to read progress from.</param>
    /// <returns>A JSON progress payload, <c>"null"</c> when no progress exists, or <see langword="null"/> when the operation fails.</returns>
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

            if (IsVideoLikeMediaType(mediaType))
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

                var responseMediaType = ToVideoLikeMediaTypeString(mediaType);
                var payload = new MediaProgressResponse(
                    responseMediaType,
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

    /// <summary>
    /// Persists paged-media progress for a user.
    /// </summary>
    /// <param name="mediaId">The media identifier to update.</param>
    /// <param name="pluginId">The plugin identifier associated with the media item.</param>
    /// <param name="chapterId">The active chapter identifier.</param>
    /// <param name="pageIndex">The zero-based page index reached by the user.</param>
    /// <param name="completed">Whether the chapter or item is considered completed.</param>
    /// <param name="userId">The user or library scope to store progress under.</param>
    /// <returns><c>1</c> when progress is stored; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Persists video playback progress for a user.
    /// </summary>
    /// <param name="mediaId">The media identifier to update.</param>
    /// <param name="pluginId">The plugin identifier associated with the media item.</param>
    /// <param name="positionSeconds">The playback position in seconds.</param>
    /// <param name="completed">Whether playback is considered completed.</param>
    /// <param name="userId">The user or library scope to store progress under.</param>
    /// <returns><c>1</c> when progress is stored; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Lists read chapter identifiers for a media item as JSON.
    /// </summary>
    /// <param name="mediaId">The media identifier to inspect.</param>
    /// <param name="pluginId">The plugin identifier associated with the media item.</param>
    /// <param name="userId">The user or library scope to inspect.</param>
    /// <returns>A JSON array of chapter identifiers, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Lists history entries as JSON.
    /// </summary>
    /// <param name="limit">The maximum number of history entries to return.</param>
    /// <param name="userId">The user or library scope to inspect.</param>
    /// <returns>A JSON array of history entries, or <see langword="null"/> when the operation fails.</returns>
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

    /// <summary>
    /// Deletes history entries for a media item and plugin combination.
    /// </summary>
    /// <param name="mediaId">The media identifier to clear history for.</param>
    /// <param name="pluginId">The plugin identifier associated with the media item.</param>
    /// <param name="userId">The user or library scope to clear history from.</param>
    /// <returns><c>1</c> when history is deleted; otherwise <c>0</c>.</returns>
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

    /// <summary>
    /// Lists orphaned media items that no longer resolve to a healthy source plugin.
    /// </summary>
    /// <param name="userId">The user or library scope to inspect.</param>
    /// <returns>A JSON array of orphaned media items, or <see langword="null"/> when the operation fails.</returns>
    public static string? GetMigrationOrphanedMediaJsonManaged(string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            var serviceProvider = _serviceProvider!;

            var normalizedUserId = string.IsNullOrWhiteSpace(userId)
                ? DefaultProgressUserId
                : userId;

            var library = serviceProvider.GetRequiredService<ILibraryPort>();
            var catalog = serviceProvider.GetRequiredService<IMediaCatalogPort>();
            var history = serviceProvider.GetRequiredService<IHistoryPort>();
            var progress = serviceProvider.GetRequiredService<IProgressPort>();

            var libraryKeys = library.ListLibrariesAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var historyEntries = history.GetHistoryAsync(normalizedUserId, 10000, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var availabilityCache = new Dictionary<string, MigrationPluginAvailability>(StringComparer.OrdinalIgnoreCase);
            var accumulators = new Dictionary<string, MigrationOrphanAccumulator>(StringComparer.Ordinal);

            foreach (var libraryKey in libraryKeys)
            {
                var entries = library.GetLibraryAsync(libraryKey, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                var displayLibrary = FromLibraryStorageKey(libraryKey);

                foreach (var entry in entries)
                {
                    var metadata = catalog.GetMediaAsync(entry.MediaId, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    var sourceId = !string.IsNullOrWhiteSpace(metadata?.SourceId)
                        ? metadata!.SourceId
                        : entry.SourceId;

                    var availability = ResolveMigrationPluginAvailability(sourceId, availabilityCache);
                    if (!availability.IsOrphaned)
                    {
                        continue;
                    }

                    var accumulator = GetOrCreateMigrationAccumulator(
                        accumulators,
                        entry.MediaId.Value,
                        sourceId,
                        metadata,
                        availability.Reason);
                    accumulator.Libraries.Add(displayLibrary);
                    accumulator.ApplyMetadata(metadata);
                }
            }

            foreach (var entry in historyEntries)
            {
                var availability = ResolveMigrationPluginAvailability(entry.PluginId, availabilityCache);
                if (!availability.IsOrphaned)
                {
                    continue;
                }

                var mediaId = entry.MediaId.Value;
                var metadata = catalog.GetMediaAsync(entry.MediaId, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                var accumulator = GetOrCreateMigrationAccumulator(
                    accumulators,
                    mediaId,
                    entry.PluginId,
                    metadata,
                    availability.Reason);
                accumulator.ApplyMetadata(metadata);
                accumulator.ConsiderHistory(entry);
            }

            var results = accumulators.Values
                .Select(accumulator => BuildMigrationOrphanResponse(accumulator, progress, normalizedUserId))
                .OrderByDescending(static item => item.LatestHistory?.LastViewedAtUtc ?? item.Progress?.LastViewedAtUtc ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            IReadOnlyList<MigrationOrphanedItemResponse> payload = results;
            return JsonSerializer.Serialize(
                payload,
                PluginHostExportsJsonContext.Default.IReadOnlyListMigrationOrphanedItemResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    /// <summary>
    /// Checks whether an orphaned media item can migrate to a target plugin.
    /// </summary>
    /// <param name="requestJson">The serialized migration check request.</param>
    /// <param name="userId">The user or library scope to inspect.</param>
    /// <returns>A JSON migration check payload, or <see langword="null"/> when the operation fails.</returns>
    public static string? CheckMigrationJsonManaged(
        string requestJson,
        string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(requestJson))
            {
                SetLastError("Migration check payload is required.");
                return null;
            }

            var request = JsonSerializer.Deserialize(
                requestJson,
                PluginHostExportsJsonContext.Default.MigrationCheckRequest);
            if (request is null)
            {
                SetLastError("Invalid migration check payload.");
                return null;
            }

            var response = BuildMigrationCheckResponse(
                request.MediaId,
                request.OrphanedPluginId,
                request.Title,
                request.MediaType,
                request.TargetPluginId,
                request.QueryOverride,
                request.TargetMediaId,
                string.IsNullOrWhiteSpace(userId) ? DefaultProgressUserId : userId);

            return JsonSerializer.Serialize(
                response,
                PluginHostExportsJsonContext.Default.MigrationCheckResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    /// <summary>
    /// Executes a migration from an orphaned media item to a target plugin.
    /// </summary>
    /// <param name="requestJson">The serialized migration execution request.</param>
    /// <param name="userId">The user or library scope to mutate.</param>
    /// <returns>A JSON execution payload, or <see langword="null"/> when the operation fails.</returns>
    public static string? ExecuteMigrationJsonManaged(
        string requestJson,
        string userId = DefaultProgressUserId)
    {
        ClearLastError();

        try
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(requestJson))
            {
                SetLastError("Migration execution payload is required.");
                return null;
            }

            var request = JsonSerializer.Deserialize(
                requestJson,
                PluginHostExportsJsonContext.Default.MigrationExecutionRequest);
            if (request is null)
            {
                SetLastError("Invalid migration execution payload.");
                return null;
            }

            var response = ExecuteMigrationInternal(
                request,
                string.IsNullOrWhiteSpace(userId) ? DefaultProgressUserId : userId);

            return JsonSerializer.Serialize(
                response,
                PluginHostExportsJsonContext.Default.MigrationExecutionResponse);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    // ==================== FFI Boundary (UnmanagedCallersOnly) ====================

    /// <summary>
    /// Initializes the embedded plugin host from unmanaged code.
    /// </summary>
    /// <param name="manifestsDirUtf8">A UTF-8 pointer to the manifest directory path.</param>
    /// <param name="sandboxDirUtf8">A UTF-8 pointer to the sandbox directory path.</param>
    /// <returns><c>0</c> when initialization succeeds; otherwise <c>-1</c>.</returns>
    [UnmanagedCallersOnly(EntryPoint = "plugin_host_initialize")]
    public static int Initialize(IntPtr manifestsDirUtf8, IntPtr sandboxDirUtf8)
    {
        var manifestsDir = PtrToString(manifestsDirUtf8) ?? "manifests";
        var sandboxDir = PtrToString(sandboxDirUtf8) ?? "sandbox";
        return InitializeManaged(manifestsDir, sandboxDir);
    }

    /// <summary>
    /// Shuts down the embedded plugin host from unmanaged code.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "plugin_host_shutdown")]
    public static void Shutdown()
    {
        ShutdownManaged();
    }

    /// <summary>
    /// Lists discovered plugins for unmanaged callers.
    /// </summary>
    /// <returns>A UTF-8 string pointer containing plugin JSON, or <see cref="IntPtr.Zero"/> on failure.</returns>
    [UnmanagedCallersOnly(EntryPoint = "plugin_host_list_plugins_json")]
    public static IntPtr ListPluginsJson()
    {
        var json = ListPluginsJsonManaged();
        return json != null ? AllocUtf8(json) : IntPtr.Zero;
    }

    /// <summary>
    /// Executes a plugin search for unmanaged callers.
    /// </summary>
    /// <param name="pluginIdUtf8">A UTF-8 pointer to the plugin identifier.</param>
    /// <param name="queryUtf8">A UTF-8 pointer to the search query.</param>
    /// <returns>A UTF-8 string pointer containing search JSON, or <see cref="IntPtr.Zero"/> on failure.</returns>
    [UnmanagedCallersOnly(EntryPoint = "plugin_host_search_json")]
    public static IntPtr SearchJson(IntPtr pluginIdUtf8, IntPtr queryUtf8)
    {
        var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;
        var query = PtrToString(queryUtf8) ?? string.Empty;
        var json = SearchJsonManaged(pluginId, query);
        return json != null ? AllocUtf8(json) : IntPtr.Zero;
    }

    /// <summary>
    /// Gets the last host error for unmanaged callers.
    /// </summary>
    /// <returns>A UTF-8 string pointer containing the last error, or <see cref="IntPtr.Zero"/> when no error is available.</returns>
    [UnmanagedCallersOnly(EntryPoint = "plugin_host_last_error")]
    public static IntPtr LastError()
    {
        var error = GetLastErrorManaged();
        return error != null ? AllocUtf8(error) : IntPtr.Zero;
    }

    /// <summary>
    /// Frees a UTF-8 string allocated by the unmanaged host boundary.
    /// </summary>
    /// <param name="value">The pointer previously returned by a host export.</param>
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

        return GetRemoteVideoStreamsManagedInternal(pluginId, mediaId);
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

    private static string? ResolveThumbnailUrlFromSearch(string sourceId, string mediaId, string title)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        try
        {
            var query = string.IsNullOrWhiteSpace(title)
                ? mediaId
                : title;
            var json = SearchJsonManaged(sourceId, query, Guid.NewGuid().ToString("n"));
            if (string.IsNullOrWhiteSpace(json))
            {
                ClearLastError();
                return null;
            }

            var parsed = JsonSerializer.Deserialize(json, PluginHostExportsJsonContext.Default.IReadOnlyListMediaSummary);
            if (parsed is null || parsed.Count == 0)
            {
                return null;
            }

            var mediaIdMatch = parsed.FirstOrDefault(item =>
                string.Equals(item.Id.Value, mediaId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(mediaIdMatch?.ThumbnailUrl))
            {
                return mediaIdMatch!.ThumbnailUrl;
            }

            var normalizedTitle = (title ?? string.Empty).Trim();
            if (normalizedTitle.Length > 0)
            {
                var titleMatch = parsed.FirstOrDefault(item =>
                    string.Equals(item.Title, normalizedTitle, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(titleMatch?.ThumbnailUrl))
                {
                    return titleMatch!.ThumbnailUrl;
                }
            }

            return parsed
                .Select(item => item.ThumbnailUrl)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }
        catch
        {
            return null;
        }
        finally
        {
            // Search failures during refresh should not poison the caller error state.
            ClearLastError();
        }
    }

    private static GrpcChannel GetOrCreateChannel(Uri address)
    {
        return _grpcChannelCache.GetOrAdd(address.ToString(), _ => GrpcChannel.ForAddress(address));
    }

    private static MediaSummary MapPluginSearchSummary(PluginContracts.MediaSummary result)
    {
        var mediaType = ParseMediaType(result.MediaType);

        var thumbnailUrl = string.IsNullOrWhiteSpace(result.ThumbnailUrl)
            ? null
            : result.ThumbnailUrl;

        var description = string.IsNullOrWhiteSpace(result.Description)
            ? null
            : result.Description;

        IReadOnlyDictionary<string, string>? metadata = null;
        if (result.Metadata?.Count > 0)
        {
            var metadataDict = new Dictionary<string, string>();
            foreach (var kvp in result.Metadata)
            {
                metadataDict[kvp.Key] = kvp.Value;
            }
            metadata = metadataDict;
        }

        return new MediaSummary(
            MediaId.Create(result.Id ?? string.Empty),
            result.Source ?? string.Empty,
            result.Title ?? string.Empty,
            mediaType,
            thumbnailUrl,
            description,
            metadata);
    }

    private static MediaSummary? EnrichGrpcPluginSearchMedia(string pluginId, Uri address, MediaSummary media)
    {
        var correlationId = Guid.NewGuid().ToString("n");
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        var channel = GetOrCreateChannel(address);
        var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
        var headers = BuildGrpcHeaders(pluginId, correlationId);

        var request = new PluginContracts.EnrichSearchItemsRequest
        {
            Context = new PluginContracts.RequestContext
            {
                CorrelationId = correlationId,
                DeadlineUtc = deadlineUtc.ToString("O")
            }
        };

        request.Items.Add(MapPluginSearchContract(media));
        var response = client.EnrichSearchItemsAsync(request, headers: headers, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var enriched = response.Results.FirstOrDefault();
        return enriched is null ? media : MapPluginSearchSummary(enriched);
    }

    private static IReadOnlyList<SearchSuggestionItem> SearchGrpcPluginSuggestions(
        string pluginId,
        Uri? address,
        SearchSuggestionRequest request,
        string? correlationId)
    {
        if (address is null)
        {
            SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
            return [];
        }

        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("n")
            : correlationId;
        var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        var channel = GetOrCreateChannel(address);
        var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
        var headers = BuildGrpcHeaders(pluginId, resolvedCorrelationId!);

        var grpcRequest = new PluginContracts.SearchSuggestionsRequest
        {
            ControlId = request.ControlId,
            Query = request.Query,
            Limit = request.Limit ?? 0,
            Context = new PluginContracts.RequestContext
            {
                CorrelationId = resolvedCorrelationId,
                DeadlineUtc = deadlineUtc.ToString("O")
            }
        };

        if (request.SearchQuery is { } searchQuery)
        {
            grpcRequest.Search = BuildGrpcSearchRequest(searchQuery, resolvedCorrelationId!, deadlineUtc);
        }

        var response = client.SearchSuggestionsAsync(grpcRequest, headers: headers, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return [.. response.Suggestions.Select(static suggestion => new SearchSuggestionItem(
            suggestion.Value ?? string.Empty,
            suggestion.Label ?? string.Empty,
            string.IsNullOrWhiteSpace(suggestion.Description) ? null : suggestion.Description))];
    }

    private static PluginContracts.SearchRequest BuildGrpcSearchRequest(
        PluginSearchQuery query,
        string correlationId,
        DateTimeOffset deadlineUtc)
    {
        var request = new PluginContracts.SearchRequest
        {
            Query = query.Query ?? string.Empty,
            Context = new PluginContracts.RequestContext
            {
                CorrelationId = correlationId,
                DeadlineUtc = deadlineUtc.ToString("O")
            }
        };

        if (query.MediaTypes.Count > 0)
        {
            request.MediaTypes.AddRange(query.MediaTypes);
        }

        foreach (var filter in query.Filters)
        {
            var grpcFilter = new PluginContracts.SearchFilter
            {
                Id = filter.Id,
                Operation = filter.Operation ?? string.Empty
            };
            grpcFilter.Values.AddRange(filter.Values);
            request.Filters.Add(grpcFilter);
        }

        foreach (var addition in query.QueryAdditions)
        {
            request.QueryAdditions.Add(new PluginContracts.SearchQueryAddition
            {
                Id = addition.Id,
                Value = addition.Value,
                Type = addition.Type ?? string.Empty
            });
        }

        if (!string.IsNullOrWhiteSpace(query.Sort))
        {
            request.Sort = query.Sort;
        }

        if (query.Page is int page && page >= 0)
        {
            request.Page = page;
        }

        if (query.PageSize is int pageSize && pageSize > 0)
        {
            request.PageSize = pageSize;
        }

        return request;
    }

    private static PluginContracts.MediaSummary MapPluginSearchContract(MediaSummary media)
    {
        var result = new PluginContracts.MediaSummary
        {
            Id = media.Id.Value,
            Source = media.SourceId,
            Title = media.Title,
            MediaType = ToMediaTypeString(media.MediaType),
            ThumbnailUrl = media.ThumbnailUrl ?? string.Empty,
            Description = media.Description ?? string.Empty
        };

        if (media.Metadata is { Count: > 0 })
        {
            result.Metadata.AddRange(media.Metadata.Select(static entry => new PluginContracts.KeyValue
            {
                Key = entry.Key,
                Value = entry.Value
            }));
        }

        return result;
    }

    private static Uri BuildPluginHostUri(Uri baseUri, string path)
    {
        var combinedPath = $"{baseUri.AbsolutePath.TrimEnd('/')}/{path.TrimStart('/')}";
        if (!combinedPath.StartsWith('/'))
        {
            combinedPath = "/" + combinedPath;
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = combinedPath,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private static MediaType ParseMediaType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, "video", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Video;
        }

        if (string.Equals(normalized, "audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "music", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "podcast", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Audio;
        }

        return MediaType.Paged;
    }

    private static bool IsVideoLikeMediaType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "music", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "podcast", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToMediaTypeString(MediaType mediaType)
    {
        return mediaType switch
        {
            MediaType.Video => "video",
            MediaType.Audio => "audio",
            _ => "paged"
        };
    }

    private static string ToVideoLikeMediaTypeString(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, "audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "music", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "podcast", StringComparison.OrdinalIgnoreCase))
        {
            return "audio";
        }

        return "video";
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

    private static string SerializeMetadataAttributes(Dictionary<string, object?> metadata)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var entry in metadata)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                WriteJsonValue(writer, entry.Key, entry.Value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static MigrationCheckResponse BuildMigrationCheckResponse(
        string mediaId,
        string orphanedPluginId,
        string title,
        string mediaType,
        string targetPluginId,
        string? queryOverride,
        string? targetMediaId,
        string userId)
    {
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            throw new InvalidOperationException("Media ID is required.");
        }

        if (string.IsNullOrWhiteSpace(targetPluginId))
        {
            throw new InvalidOperationException("Target plugin ID is required.");
        }

        if (!TryResolvePlugin(targetPluginId, out _, out _))
        {
            throw new InvalidOperationException(GetLastErrorManaged() ?? "Target plugin could not be opened.");
        }

        var sourceState = LoadMigrationSourceState(mediaId, orphanedPluginId, mediaType, userId);
        var matchQuery = string.IsNullOrWhiteSpace(queryOverride)
            ? (!string.IsNullOrWhiteSpace(title) ? title : mediaId)
            : queryOverride.Trim();

        var searchResults = SearchMediaManaged(targetPluginId, matchQuery) ?? [];
        var matches = searchResults
            .Where(result => IsMigrationMatchCandidate(result, title, mediaType, targetMediaId))
            .Select(result => new
            {
                Media = result,
                Score = ScoreMigrationMatch(result, title, mediaType, targetMediaId)
            })
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Media.Title, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var payloadMatches = matches
            .Select(item => new MigrationMatchResponse(
                item.Media.Id.Value,
                item.Media.Title,
                ToMediaTypeString(item.Media.MediaType),
                item.Media.ThumbnailUrl,
                item.Score,
                string.Equals(NormalizeMigrationText(item.Media.Title), NormalizeMigrationText(title), StringComparison.Ordinal)))
            .ToList();

        var selected = matches
            .Select(static item => item.Media)
            .FirstOrDefault(media => !string.IsNullOrWhiteSpace(targetMediaId)
                ? string.Equals(media.Id.Value, targetMediaId, StringComparison.Ordinal)
                : true);

        var blockers = new List<string>();
        var warnings = new List<string>();

        if (selected is null)
        {
            blockers.Add("No sufficiently similar media item was found in the selected target plugin.");
            return new MigrationCheckResponse(
                false,
                null,
                null,
                null,
                null,
                sourceState.Libraries.Count > 0,
                false,
                false,
                sourceState.Libraries,
                warnings,
                blockers,
                payloadMatches);
        }

        var canMigrateLibrary = sourceState.Libraries.Count > 0;
        var canMigrateHistory = false;
        var canMigrateProgress = false;

        if (sourceState.HistoryEntries.Count == 0)
        {
            warnings.Add("No persisted history entries were found for this orphaned media item.");
        }

        if (IsVideoLikeMediaType(mediaType))
        {
            canMigrateHistory = sourceState.HistoryEntries.Count > 0;
            canMigrateProgress = sourceState.Progress is not null || sourceState.HistoryEntries.Count > 0;
        }
        else
        {
            var targetChapters = GetChaptersManagedInternal(targetPluginId, selected.Id.Value, forceRefresh: false) ?? [];
            var targetChapterIds = targetChapters
                .Select(static chapter => chapter.ChapterId)
                .Where(static chapterId => !string.IsNullOrWhiteSpace(chapterId))
                .ToHashSet(StringComparer.Ordinal);

            var sourceChapterIds = sourceState.HistoryEntries
                .Where(static entry => entry.EntryId.StartsWith("paged::", StringComparison.Ordinal))
                .Select(static entry => entry.ExternalId)
                .Where(static externalId => !string.IsNullOrWhiteSpace(externalId))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var mappedHistoryCount = sourceChapterIds.Count(chapterId => targetChapterIds.Contains(chapterId));
            canMigrateHistory = mappedHistoryCount > 0;
            if (sourceChapterIds.Count > 0 && mappedHistoryCount < sourceChapterIds.Count)
            {
                warnings.Add($"Only {mappedHistoryCount} of {sourceChapterIds.Count} chapter history entries matched exact chapter IDs in the target plugin.");
            }

            if (sourceState.Progress is null)
            {
                warnings.Add("No persisted paged progress was found for this orphaned media item.");
            }
            else if (!string.IsNullOrWhiteSpace(sourceState.Progress.ChapterId)
                     && targetChapterIds.Contains(sourceState.Progress.ChapterId))
            {
                canMigrateProgress = true;
            }
            else if (!string.IsNullOrWhiteSpace(sourceState.Progress.ChapterId))
            {
                blockers.Add("Current paged progress chapter ID does not exist in the target plugin.");
            }
        }

        return new MigrationCheckResponse(
            true,
            selected.Id.Value,
            selected.Title,
            ToMediaTypeString(selected.MediaType),
            selected.ThumbnailUrl,
            canMigrateLibrary,
            canMigrateProgress,
            canMigrateHistory,
            sourceState.Libraries,
            warnings,
            blockers,
            payloadMatches);
    }

    private static MigrationExecutionResponse ExecuteMigrationInternal(
        MigrationExecutionRequest request,
        string userId)
    {
        var check = BuildMigrationCheckResponse(
            request.MediaId,
            request.OrphanedPluginId,
            request.Title,
            request.MediaType,
            request.TargetPluginId,
            request.QueryOverride,
            request.TargetMediaId,
            userId);

        if (!check.HasMatch || string.IsNullOrWhiteSpace(check.SelectedTargetMediaId))
        {
            return new MigrationExecutionResponse(
                false,
                null,
                0,
                0,
                0,
                false,
                check.Warnings,
                check.Blockers.Count > 0 ? check.Blockers : ["No target media match was selected."]);
        }

        var matchQuery = string.IsNullOrWhiteSpace(request.QueryOverride)
            ? (!string.IsNullOrWhiteSpace(request.Title) ? request.Title : request.MediaId)
            : request.QueryOverride.Trim();
        var targetMedia = (SearchMediaManaged(request.TargetPluginId, matchQuery) ?? [])
            .FirstOrDefault(media => string.Equals(media.Id.Value, check.SelectedTargetMediaId, StringComparison.Ordinal));
        if (targetMedia is null)
        {
            return new MigrationExecutionResponse(
                false,
                check.SelectedTargetMediaId,
                0,
                0,
                0,
                false,
                check.Warnings,
                ["Target media could not be resolved before migration was executed."]);
        }

        var sourceState = LoadMigrationSourceState(request.MediaId, request.OrphanedPluginId, request.MediaType, userId);
        var warnings = new List<string>(check.Warnings);
        var errors = new List<string>();
        var librariesAdded = 0;
        var historyEntriesMigrated = 0;
        var historyEntriesSkipped = 0;

        foreach (var libraryName in sourceState.Libraries)
        {
            var added = AddMediaToLibraryManaged(
                targetMedia.Id.Value,
                request.TargetPluginId,
                targetMedia.Title,
                ToMediaTypeString(targetMedia.MediaType),
                libraryName,
                BuildMigrationDescriptionPayload(targetMedia.Description, targetMedia.Metadata),
                targetMedia.ThumbnailUrl);
            if (added == 0)
            {
                errors.Add($"Failed to add target media to library '{libraryName}': {GetLastErrorManaged() ?? "unknown error"}");
            }
            else
            {
                librariesAdded++;
            }
        }

        var history = _serviceProvider!.GetRequiredService<IHistoryPort>();
        IReadOnlyDictionary<string, string> pagedChapterMap = new Dictionary<string, string>();
        if (!IsVideoLikeMediaType(request.MediaType))
        {
            var targetChapters = GetChaptersManagedInternal(request.TargetPluginId, targetMedia.Id.Value, forceRefresh: false) ?? [];
            pagedChapterMap = targetChapters
                .Where(static chapter => !string.IsNullOrWhiteSpace(chapter.ChapterId))
                .ToDictionary(static chapter => chapter.ChapterId, static chapter => chapter.ChapterId, StringComparer.Ordinal);
        }

        foreach (var entry in sourceState.HistoryEntries)
        {
            string targetExternalId;
            string targetEntryId;

            if (IsVideoLikeMediaType(request.MediaType))
            {
                targetExternalId = targetMedia.Id.Value;
                targetEntryId = BuildMigrationHistoryEntryId(
                    isVideoLike: true,
                    userId: userId,
                    pluginId: request.TargetPluginId,
                    mediaId: targetMedia.Id.Value,
                    externalId: targetExternalId);
            }
            else
            {
                if (!pagedChapterMap.TryGetValue(entry.ExternalId, out targetExternalId!))
                {
                    historyEntriesSkipped++;
                    continue;
                }

                targetEntryId = BuildMigrationHistoryEntryId(
                    isVideoLike: false,
                    userId: userId,
                    pluginId: request.TargetPluginId,
                    mediaId: targetMedia.Id.Value,
                    externalId: targetExternalId);
            }

            history.UpsertAsync(
                    new MediaHistoryEntry(
                        targetEntryId,
                        MediaId.Create(targetMedia.Id.Value),
                        request.TargetPluginId,
                        targetExternalId,
                        userId,
                        entry.Position,
                        entry.Completed,
                        entry.LastViewedAtUtc),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            historyEntriesMigrated++;
        }

        var canRemoveSource = request.RemoveOrphanedEntriesAfterSuccess
            && errors.Count == 0
            && historyEntriesSkipped == 0;
        var sourceEntriesRemoved = false;

        if (request.RemoveOrphanedEntriesAfterSuccess && !canRemoveSource)
        {
            warnings.Add("Source cleanup was skipped because the migration completed only partially.");
        }

        if (canRemoveSource)
        {
            foreach (var libraryName in sourceState.Libraries)
            {
                RemoveMediaFromLibraryManaged(request.MediaId, libraryName);
            }

            DeleteHistoryForMediaManaged(request.MediaId, request.OrphanedPluginId, userId);
            sourceEntriesRemoved = true;
        }

        return new MigrationExecutionResponse(
            errors.Count == 0,
            targetMedia.Id.Value,
            librariesAdded,
            historyEntriesMigrated,
            historyEntriesSkipped,
            sourceEntriesRemoved,
            warnings,
            errors);
    }

    private static MigrationSourceState LoadMigrationSourceState(
        string mediaId,
        string orphanedPluginId,
        string mediaType,
        string userId)
    {
        var serviceProvider = _serviceProvider!;
        var library = serviceProvider.GetRequiredService<ILibraryPort>();
        var history = serviceProvider.GetRequiredService<IHistoryPort>();
        var progress = serviceProvider.GetRequiredService<IProgressPort>();

        var libraries = library.ListLibrariesAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .Select(FromLibraryStorageKey)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => library.GetLibraryAsync(ToLibraryStorageKey(name), CancellationToken.None)
                .GetAwaiter()
                .GetResult()
                .Any(entry => string.Equals(entry.MediaId.Value, mediaId, StringComparison.Ordinal)
                              && string.Equals(entry.SourceId, orphanedPluginId ?? string.Empty, StringComparison.Ordinal)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var historyEntries = history.GetHistoryAsync(userId, 10000, CancellationToken.None)
            .GetAwaiter()
            .GetResult()
            .Where(entry => string.Equals(entry.MediaId.Value, mediaId, StringComparison.Ordinal))
            .Where(entry => string.Equals(entry.PluginId, orphanedPluginId ?? string.Empty, StringComparison.Ordinal))
            .OrderByDescending(static entry => entry.LastViewedAtUtc)
            .ToList();

        var resolvedMediaType = NormalizeMigrationMediaType(mediaType, historyEntries.FirstOrDefault()?.EntryId);
        MediaProgressResponse? progressPayload = null;
        if (IsVideoLikeMediaType(resolvedMediaType))
        {
            var video = progress.GetVideoProgressAsync(
                    MediaId.Create(mediaId),
                    orphanedPluginId ?? string.Empty,
                    userId,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (video is not null)
            {
                progressPayload = new MediaProgressResponse(
                    ToVideoLikeMediaTypeString(resolvedMediaType),
                    null,
                    null,
                    video.PositionSeconds,
                    video.Completed,
                    video.LastViewedAtUtc.ToString("O"));
            }
        }
        else
        {
            var paged = progress.GetPagedProgressAsync(
                    MediaId.Create(mediaId),
                    orphanedPluginId ?? string.Empty,
                    userId,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (paged is not null)
            {
                progressPayload = new MediaProgressResponse(
                    "paged",
                    paged.ChapterId,
                    paged.PageIndex,
                    null,
                    paged.Completed,
                    paged.LastViewedAtUtc.ToString("O"));
            }
        }

        return new MigrationSourceState(libraries, historyEntries, progressPayload, resolvedMediaType);
    }

    private static MigrationOrphanedItemResponse BuildMigrationOrphanResponse(
        MigrationOrphanAccumulator accumulator,
        IProgressPort progress,
        string userId)
    {
        var resolvedMediaType = NormalizeMigrationMediaType(accumulator.MediaType, accumulator.LatestHistory?.EntryId);
        MediaProgressResponse? progressPayload = null;

        if (IsVideoLikeMediaType(resolvedMediaType))
        {
            var video = progress.GetVideoProgressAsync(
                    MediaId.Create(accumulator.MediaId),
                    accumulator.OrphanedPluginId,
                    userId,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (video is not null)
            {
                progressPayload = new MediaProgressResponse(
                    ToVideoLikeMediaTypeString(resolvedMediaType),
                    null,
                    null,
                    video.PositionSeconds,
                    video.Completed,
                    video.LastViewedAtUtc.ToString("O"));
            }
        }
        else
        {
            var paged = progress.GetPagedProgressAsync(
                    MediaId.Create(accumulator.MediaId),
                    accumulator.OrphanedPluginId,
                    userId,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (paged is not null)
            {
                progressPayload = new MediaProgressResponse(
                    "paged",
                    paged.ChapterId,
                    paged.PageIndex,
                    null,
                    paged.Completed,
                    paged.LastViewedAtUtc.ToString("O"));
            }
        }

        return new MigrationOrphanedItemResponse(
            accumulator.MediaId,
            accumulator.OrphanedPluginId,
            string.IsNullOrWhiteSpace(accumulator.Title) ? accumulator.MediaId : accumulator.Title,
            resolvedMediaType,
            accumulator.ThumbnailUrl,
            accumulator.Description,
            accumulator.Metadata.Count == 0 ? null : accumulator.Metadata,
            accumulator.Libraries.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToList(),
            progressPayload,
            accumulator.LatestHistory is null ? null : new HistoryEntryResponse(
                accumulator.LatestHistory.EntryId,
                accumulator.LatestHistory.MediaId.Value,
                accumulator.LatestHistory.PluginId,
                accumulator.LatestHistory.ExternalId,
                accumulator.LatestHistory.UserId,
                accumulator.LatestHistory.Position,
                accumulator.LatestHistory.Completed,
                accumulator.LatestHistory.LastViewedAtUtc.ToString("O")),
            accumulator.OrphanReason);
    }

    private static MigrationOrphanAccumulator GetOrCreateMigrationAccumulator(
        IDictionary<string, MigrationOrphanAccumulator> accumulators,
        string mediaId,
        string orphanedPluginId,
        MediaMetadata? metadata,
        string orphanReason)
    {
        var key = $"{orphanedPluginId}\u001f{mediaId}";
        if (accumulators.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = new MigrationOrphanAccumulator(
            mediaId,
            orphanedPluginId ?? string.Empty,
            metadata?.Title ?? mediaId,
            metadata is null ? null : ToMediaTypeString(metadata.MediaType),
            metadata?.ThumbnailUrl,
            metadata?.Synopsis,
            ParseMetadataAttributes(metadata?.Attributes),
            orphanReason);
        accumulators[key] = created;
        return created;
    }

    private static MigrationPluginAvailability ResolveMigrationPluginAvailability(
        string pluginId,
        IDictionary<string, MigrationPluginAvailability> cache)
    {
        var key = pluginId ?? string.Empty;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        MigrationPluginAvailability resolved;
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            resolved = new MigrationPluginAvailability(true, "Missing source/plugin ID.");
        }
        else
        {
            ClearLastError();
            var isHealthy = TryResolvePlugin(pluginId, out _, out _);
            var error = GetLastErrorManaged();
            ClearLastError();

            resolved = isHealthy
                ? new MigrationPluginAvailability(false, string.Empty)
                : new MigrationPluginAvailability(
                    true,
                    string.IsNullOrWhiteSpace(error)
                        ? $"Plugin '{pluginId}' is unavailable."
                        : error);
        }

        cache[key] = resolved;
        return resolved;
    }

    private static IReadOnlyDictionary<string, string> ParseMetadataAttributes(string? attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(attributes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>();
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Array => string.Join(", ", property.Value.EnumerateArray().Select(static item => item.ToString())),
                    _ => property.Value.ToString(),
                };

                if (!string.IsNullOrWhiteSpace(value))
                {
                    result[property.Name] = value;
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static string NormalizeMigrationMediaType(string? mediaType, string? entryId)
    {
        if (IsVideoLikeMediaType(mediaType))
        {
            return ToVideoLikeMediaTypeString(mediaType);
        }

        if (string.Equals(mediaType, "paged", StringComparison.OrdinalIgnoreCase))
        {
            return "paged";
        }

        if (!string.IsNullOrWhiteSpace(entryId) && entryId.StartsWith("video::", StringComparison.Ordinal))
        {
            return "video";
        }

        return "paged";
    }

    private static int ScoreMigrationMatch(
        MediaSummary candidate,
        string sourceTitle,
        string sourceMediaType,
        string? requestedTargetMediaId)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(requestedTargetMediaId)
            && string.Equals(candidate.Id.Value, requestedTargetMediaId, StringComparison.Ordinal))
        {
            score += 1000;
        }

        var normalizedSourceTitle = NormalizeMigrationText(sourceTitle);
        var normalizedCandidateTitle = NormalizeMigrationText(candidate.Title);
        if (!string.IsNullOrWhiteSpace(normalizedSourceTitle)
            && string.Equals(normalizedSourceTitle, normalizedCandidateTitle, StringComparison.Ordinal))
        {
            score += 500;
        }
        else if (!string.IsNullOrWhiteSpace(normalizedSourceTitle)
                 && !string.IsNullOrWhiteSpace(normalizedCandidateTitle)
                 && (normalizedCandidateTitle.Contains(normalizedSourceTitle, StringComparison.Ordinal)
                     || normalizedSourceTitle.Contains(normalizedCandidateTitle, StringComparison.Ordinal)))
        {
            score += 250;
        }

        var sourceTokens = normalizedSourceTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var candidateTokens = normalizedCandidateTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        score += candidateTokens.Count(sourceTokens.Contains) * 25;

        if (string.Equals(ToMediaTypeString(candidate.MediaType), NormalizeMigrationMediaType(sourceMediaType, null), StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        return score;
    }

    private static bool IsMigrationMatchCandidate(
        MediaSummary candidate,
        string sourceTitle,
        string sourceMediaType,
        string? requestedTargetMediaId)
    {
        if (!string.IsNullOrWhiteSpace(requestedTargetMediaId)
            && string.Equals(candidate.Id.Value, requestedTargetMediaId, StringComparison.Ordinal))
        {
            return true;
        }

        var normalizedSourceTitle = NormalizeMigrationText(sourceTitle);
        var normalizedCandidateTitle = NormalizeMigrationText(candidate.Title);
        if (string.IsNullOrWhiteSpace(normalizedSourceTitle)
            || string.IsNullOrWhiteSpace(normalizedCandidateTitle))
        {
            return false;
        }

        if (string.Equals(normalizedSourceTitle, normalizedCandidateTitle, StringComparison.Ordinal))
        {
            return true;
        }

        if (normalizedCandidateTitle.Contains(normalizedSourceTitle, StringComparison.Ordinal)
            || normalizedSourceTitle.Contains(normalizedCandidateTitle, StringComparison.Ordinal))
        {
            return true;
        }

        var sourceTokens = normalizedSourceTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var candidateTokens = normalizedCandidateTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (sourceTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return false;
        }

        var sharedTokenCount = sourceTokens.Intersect(candidateTokens, StringComparer.Ordinal).Count();
        if (sharedTokenCount == 0)
        {
            return false;
        }

        var smallerTokenCount = Math.Min(sourceTokens.Count, candidateTokens.Count);
        var largerTokenCount = Math.Max(sourceTokens.Count, candidateTokens.Count);
        var sharedCoverage = (double)sharedTokenCount / smallerTokenCount;
        var overlapRatio = (double)sharedTokenCount / largerTokenCount;
        if (smallerTokenCount == 1)
        {
            return sharedCoverage >= 1d;
        }

        if (sharedTokenCount >= 2 && (sharedCoverage >= 0.6d || overlapRatio >= 0.5d))
        {
            return true;
        }

        var candidateMediaType = ToMediaTypeString(candidate.MediaType);
        return string.Equals(candidateMediaType, NormalizeMigrationMediaType(sourceMediaType, null), StringComparison.OrdinalIgnoreCase)
               && sharedCoverage >= 0.75d;
    }

    private static string NormalizeMigrationText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        return string.Join(
            ' ',
            new string(chars)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildMigrationDescriptionPayload(
        string? description,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(description))
        {
            payload["description"] = description;
        }

        if (metadata is not null)
        {
            foreach (var entry in metadata)
            {
                payload[entry.Key] = entry.Value;
            }
        }

        return payload.Count == 0 ? string.Empty : SerializeMetadataAttributes(payload);
    }

    private static string BuildMigrationHistoryEntryId(
        bool isVideoLike,
        string userId,
        string pluginId,
        string mediaId,
        string externalId)
    {
        return isVideoLike
            ? $"video::{userId}::{pluginId}::{mediaId}"
            : $"paged::{userId}::{pluginId}::{mediaId}::{externalId}";
    }

    private sealed record MigrationPluginAvailability(bool IsOrphaned, string Reason);

    private sealed class MigrationOrphanAccumulator(
        string mediaId,
        string orphanedPluginId,
        string title,
        string? mediaType,
        string? thumbnailUrl,
        string? description,
        IReadOnlyDictionary<string, string> metadata,
        string orphanReason)
    {
        public string MediaId { get; } = mediaId;
        public string OrphanedPluginId { get; } = orphanedPluginId;
        public string Title { get; private set; } = title;
        public string? MediaType { get; private set; } = mediaType;
        public string? ThumbnailUrl { get; private set; } = thumbnailUrl;
        public string? Description { get; private set; } = description;
        public Dictionary<string, string> Metadata { get; } = new(metadata, StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Libraries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MediaHistoryEntry? LatestHistory { get; private set; }
        public string OrphanReason { get; } = orphanReason;

        public void ApplyMetadata(MediaMetadata? metadata)
        {
            if (metadata is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(Title) || string.Equals(Title, MediaId, StringComparison.Ordinal))
            {
                Title = metadata.Title;
            }

            MediaType ??= ToMediaTypeString(metadata.MediaType);
            ThumbnailUrl ??= metadata.ThumbnailUrl;
            Description ??= metadata.Synopsis;
            foreach (var entry in ParseMetadataAttributes(metadata.Attributes))
            {
                Metadata[entry.Key] = entry.Value;
            }
        }

        public void ConsiderHistory(MediaHistoryEntry entry)
        {
            if (LatestHistory is null || entry.LastViewedAtUtc > LatestHistory.LastViewedAtUtc)
            {
                LatestHistory = entry;
            }
        }
    }

    private sealed record MigrationSourceState(
        IReadOnlyList<string> Libraries,
        IReadOnlyList<MediaHistoryEntry> HistoryEntries,
        MediaProgressResponse? Progress,
        string MediaType);

    private static void WriteJsonValue(Utf8JsonWriter writer, string propertyName, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNull(propertyName);
                return;
            case string text:
                writer.WriteString(propertyName, text);
                return;
            case bool boolean:
                writer.WriteBoolean(propertyName, boolean);
                return;
            case int int32:
                writer.WriteNumber(propertyName, int32);
                return;
            case long int64:
                writer.WriteNumber(propertyName, int64);
                return;
            case double floatingPoint:
                writer.WriteNumber(propertyName, floatingPoint);
                return;
            case decimal decimalValue:
                writer.WriteNumber(propertyName, decimalValue);
                return;
            case IReadOnlyList<string> stringList:
                writer.WritePropertyName(propertyName);
                writer.WriteStartArray();
                foreach (var item in stringList)
                {
                    writer.WriteStringValue(item);
                }
                writer.WriteEndArray();
                return;
            case IEnumerable<string> enumerableStrings:
                writer.WritePropertyName(propertyName);
                writer.WriteStartArray();
                foreach (var item in enumerableStrings)
                {
                    writer.WriteStringValue(item);
                }
                writer.WriteEndArray();
                return;
            case Dictionary<string, object?> dictionary:
                writer.WritePropertyName(propertyName);
                writer.WriteStartObject();
                foreach (var nestedEntry in dictionary)
                {
                    if (string.IsNullOrWhiteSpace(nestedEntry.Key))
                    {
                        continue;
                    }

                    WriteJsonValue(writer, nestedEntry.Key, nestedEntry.Value);
                }
                writer.WriteEndObject();
                return;
            case IDictionary<string, object?> mutableDictionary:
                writer.WritePropertyName(propertyName);
                writer.WriteStartObject();
                foreach (var nestedEntry in mutableDictionary)
                {
                    if (string.IsNullOrWhiteSpace(nestedEntry.Key))
                    {
                        continue;
                    }

                    WriteJsonValue(writer, nestedEntry.Key, nestedEntry.Value);
                }
                writer.WriteEndObject();
                return;
            case IReadOnlyDictionary<string, object?> nestedDictionary:
                writer.WritePropertyName(propertyName);
                writer.WriteStartObject();
                foreach (var nestedEntry in nestedDictionary)
                {
                    if (string.IsNullOrWhiteSpace(nestedEntry.Key))
                    {
                        continue;
                    }

                    WriteJsonValue(writer, nestedEntry.Key, nestedEntry.Value);
                }
                writer.WriteEndObject();
                return;
            default:
                writer.WriteString(propertyName, value.ToString());
                return;
        }
    }

    private static string? ReadJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null,
        };
    }

    private static Dictionary<string, string>? ReadJsonObjectAsDictionary(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in property.EnumerateObject())
        {
            var key = entry.Name.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            dictionary[key] = entry.Value.ValueKind switch
            {
                JsonValueKind.String => entry.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => entry.Value.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => string.Empty,
                _ => entry.Value.ToString(),
            };
        }

        return dictionary.Count == 0 ? null : dictionary;
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
