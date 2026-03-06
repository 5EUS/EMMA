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

namespace EMMA.PluginHost.Library;

/// <summary>
/// Native FFI exports for embedding the PluginHost in-process.
/// This provides both a managed C# API and a thin FFI marshalling layer.
/// </summary>
public static class PluginHostExports
{
    private static ServiceProvider? _serviceProvider;
    private static PluginRegistry? _registry;
    private static PluginHandshakeService? _handshake;
    private static PluginResolutionService? _pluginResolution;
    private static IWasmPluginRuntimeHost? _wasmRuntime;
    private static bool _initialized = false;
    private static readonly object _initLock = new();
    private static readonly object _errorLock = new();

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
        ClearLastError();

        try
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(pluginId))
            {
                SetLastError("Plugin ID is required");
                return null;
            }

            var resolution = _pluginResolution!
                .ResolveAsync(pluginId, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var record = resolution.Record;
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

                using var channel = GrpcChannel.ForAddress(address);

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
                            : EMMA.Domain.MediaType.Paged))
                    .ToList();
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
    /// Get the last error message, or null if no error.
    /// </summary>
    public static string? GetLastErrorManaged()
    {
        lock (_errorLock)
        {
            return _lastError;
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

    private static void InitializeSqliteForEmbeddedHost()
    {
        if (OperatingSystem.IsLinux())
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
