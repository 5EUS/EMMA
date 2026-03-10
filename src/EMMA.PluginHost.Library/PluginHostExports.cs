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
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EMMA.Domain;

namespace EMMA.PluginHost.Library;

/// <summary>
/// Native FFI exports for embedding the PluginHost in-process.
/// This provides both a managed C# API and a thin FFI marshalling layer.
/// </summary>
public static class PluginHostExports
{
    private const string DefaultProgressUserId = "local";
    private const string DirectHttpEnvVar = "EMMA_WASM_DIRECT_HTTP";
    private const string InMemoryBridgeEnvVar = "EMMA_WASM_BRIDGE_IN_MEMORY_PAYLOAD";
    private const string InMemoryBridgeMaxBytesEnvVar = "EMMA_WASM_BRIDGE_IN_MEMORY_PAYLOAD_MAX_BYTES";
    private static ServiceProvider? _serviceProvider;
    private static PluginRegistry? _registry;
    private static PluginHandshakeService? _handshake;
    private static PluginResolutionService? _pluginResolution;
    private static IWasmPluginRuntimeHost? _wasmRuntime;
    private static bool _initialized = false;
    private static readonly object _initLock = new();
    private static readonly object _errorLock = new();
    private static readonly ConcurrentDictionary<string, GrpcChannel> _grpcChannelCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, MediaPage> _pageCache = new(StringComparer.Ordinal);

    // Don't use [ThreadStatic] - we need the error to be visible across threads for FFI
    private static string? _lastError;

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
                            .SetValue(options, false);
                       typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.WasmOperationTimeoutSeconds))!
                            .SetValue(options, 15);
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.WasmDirectHttp))!
                            .SetValue(options, ResolveWasmDirectHttpEnabled());
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.WasmBridgeInMemoryPayload))!
                            .SetValue(options, ResolveWasmBridgeInMemoryPayloadEnabled());
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.WasmBridgeInMemoryPayloadMaxBytes))!
                            .SetValue(options, ResolveWasmBridgeInMemoryPayloadMaxBytes());
                        typeof(PluginHostOptions).GetProperty(nameof(PluginHostOptions.SandboxEnabled))!
                            .SetValue(options, true);
                    });

                services.AddOptions<PluginSignatureOptions>();

                // Register core services
                services.AddSingleton<PluginRegistry>();
                services.AddSingleton<PluginManifestLoader>();
                services.AddSingleton<PluginPermissionSanitizer>();
                services.AddSingleton<IPluginEntrypointResolver, PluginEntrypointResolver>();
                services.AddSingleton<PluginResolutionService>();
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

                // Logging (simple console logger for now)
                services.AddLogging(builder =>
                {
                    builder.AddSimpleConsole(options =>
                    {
                        options.SingleLine = true;
                        options.IncludeScopes = false;
                    });
                    builder.SetMinimumLevel(LogLevel.Information);
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

    private static bool ResolveWasmBridgeInMemoryPayloadEnabled()
    {
        var value = Environment.GetEnvironmentVariable(InMemoryBridgeEnvVar);
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

        return false;
    }

    private static bool ResolveWasmDirectHttpEnabled()
    {
        var value = Environment.GetEnvironmentVariable(DirectHttpEnvVar);
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (bool.TryParse(value, out var parsedBool))
            {
                return parsedBool;
            }

            return value.Trim() switch
            {
                "1" or "yes" or "on" => true,
                _ => false
            };
        }

        return false;
    }

    private static int ResolveWasmBridgeInMemoryPayloadMaxBytes()
    {
        var value = Environment.GetEnvironmentVariable(InMemoryBridgeMaxBytesEnvVar);
        if (!string.IsNullOrWhiteSpace(value)
            && int.TryParse(value, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return 262_144;
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
                Author: r.Manifest.Author ?? "Unknown"
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

    /// <summary>
    /// Search for media using the specified plugin.
    /// Returns JSON array of MediaSummary, or null on error.
    /// </summary>
    public static string? SearchJsonManaged(string pluginId, string query)
    {
        var results = SearchMediaManaged(pluginId, query);
        return results is null
            ? null
            : JsonSerializer.Serialize(results, PluginHostExportsJsonContext.Default.IReadOnlyListMediaSummary);
    }

    /// <summary>
    /// Search for media using the specified plugin and return typed results.
    /// Returns null on error, check GetLastErrorManaged().
    /// </summary>
    public static IReadOnlyList<EMMA.Domain.MediaSummary>? SearchMediaManaged(string pluginId, string query)
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

            if (snapshotRecord is not null
                && _wasmRuntime!.IsWasmPlugin(snapshotRecord.Manifest)
                && snapshotRecord.Runtime.State is PluginRuntimeState.Running or PluginRuntimeState.External
                && (snapshotRecord.Runtime.State == PluginRuntimeState.External || snapshotRecord.Status.Success))
            {
                var fastResults = _wasmRuntime.SearchAsync(snapshotRecord, query ?? string.Empty, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return fastResults;
            }

            var resolution = _pluginResolution!
                .ResolveAsync(pluginId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var record = resolution.Record;
            if (record == null)
            {
                if (snapshotRecord is not null)
                {
                    SetLastError(
                        $"Plugin '{pluginId}' resolution failed before record binding. " +
                        $"runtimeState={snapshotRecord.Runtime.State}, " +
                        $"runtimeCode={snapshotRecord.Runtime.LastErrorCode ?? "none"}, " +
                        $"runtimeMessage={snapshotRecord.Runtime.LastErrorMessage ?? "none"}, " +
                        $"handshakeSuccess={snapshotRecord.Status.Success}, " +
                        $"handshakeMessage={snapshotRecord.Status.Message}");
                    return null;
                }

                SetLastError($"Plugin '{pluginId}' not found");
                return null;
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
                return null;
            }

            IReadOnlyList<EMMA.Domain.MediaSummary> results;
            if (_wasmRuntime!.IsWasmPlugin(record.Manifest))
            {
                results = _wasmRuntime.SearchAsync(record, query ?? string.Empty, CancellationToken.None)
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

                var address = resolution.Address;
                if (address is null)
                {
                    SetLastError("Plugin endpoint is missing or invalid for non-WASM plugin.");
                    return null;
                }

                var channel = GetOrCreateChannel(address);

                var correlationId = Guid.NewGuid().ToString("n");
                var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                var client = new PluginContracts.SearchProvider.SearchProviderClient(channel);
                var response = client.SearchAsync(new PluginContracts.SearchRequest
                {
                    Query = query ?? string.Empty,
                    Context = new PluginContracts.RequestContext
                    {
                        CorrelationId = correlationId,
                        DeadlineUtc = deadlineUtc.ToString("O")
                    }
                }, cancellationToken: CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                results = response.Results
                    .Select(result => new EMMA.Domain.MediaSummary(
                        EMMA.Domain.MediaId.Create(result.Id),
                        result.Source ?? string.Empty,
                        result.Title ?? string.Empty,
                        string.Equals(result.MediaType, "video", StringComparison.OrdinalIgnoreCase)
                            ? EMMA.Domain.MediaType.Video
                            : EMMA.Domain.MediaType.Paged,
                        string.IsNullOrWhiteSpace(result.ThumbnailUrl) ? null : result.ThumbnailUrl,
                        string.IsNullOrWhiteSpace(result.Description) ? null : result.Description))
                    .ToList();
            }

            return results;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
    }

    public static string? TakeLastWasmNativeTimingManaged()
    {
        return NativeInProcessWasmComponentInvoker.TakeLastNativeTimingSnapshot();
    }

    public static string? GetChaptersJsonManaged(string pluginId, string mediaId)
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
            var catalog = _serviceProvider!.GetRequiredService<IMediaCatalogPort>();
            var mediaKey = MediaId.Create(mediaId);
            var cachedRecords = catalog.GetChaptersAsync(mediaKey, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (cachedRecords.Count > 0)
            {
                var cachedChapters = cachedRecords
                    .Select(chapter => new MediaChapter(
                        chapter.ChapterId,
                        chapter.Number,
                        chapter.Title))
                    .ToList();
                return JsonSerializer.Serialize(cachedChapters, PluginHostExportsJsonContext.Default.IReadOnlyListMediaChapter);
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
                var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                var response = client.GetChaptersAsync(new PluginContracts.ChaptersRequest
                {
                    MediaId = mediaId,
                    Context = new PluginContracts.RequestContext
                    {
                        CorrelationId = correlationId,
                        DeadlineUtc = deadlineUtc.ToString("O")
                    }
                }, cancellationToken: CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

                chapters = response.Chapters
                    .Select(chapter => new MediaChapter(
                        chapter.Id ?? string.Empty,
                        chapter.Number,
                        chapter.Title ?? string.Empty))
                    .ToList();
            }

            if (chapters.Count > 0)
            {
                var chapterRecords = chapters
                    .Select(chapter => new MediaChapterRecord(
                        chapter.ChapterId,
                        mediaKey,
                        chapter.Number,
                        chapter.Title,
                        null))
                    .ToList();

                catalog.UpsertChaptersAsync(mediaKey, chapterRecords, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }

            return JsonSerializer.Serialize(chapters, PluginHostExportsJsonContext.Default.IReadOnlyListMediaChapter);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return null;
        }
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

            return JsonSerializer.Serialize(asset, PluginHostExportsJsonContext.Default.MediaPageAsset);
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
        library.NormalizeLegacyDefaultLibraryAsync(canonicalDefault, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
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
        }, cancellationToken: CancellationToken.None)
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
        }, cancellationToken: CancellationToken.None)
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

    private static string BuildPageCacheKey(string pluginId, string mediaId, string chapterId, int pageIndex)
    {
        return $"{pluginId}\u001f{mediaId}\u001f{chapterId}\u001f{pageIndex}";
    }

    private static GrpcChannel GetOrCreateChannel(Uri address)
    {
        return _grpcChannelCache.GetOrAdd(address.ToString(), _ => GrpcChannel.ForAddress(address));
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
