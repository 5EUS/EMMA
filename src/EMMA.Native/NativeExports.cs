using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Globalization;
using EMMA.Api;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;
using EMMA.PluginHost.Library;

namespace EMMA.Native;

public static class NativeExports
{
    private const string NativeConsoleLogLevelEnvVar = "EMMA_NATIVE_LOG_LEVEL";
    private const string NativeVerboseTimingEnvVar = "EMMA_NATIVE_VERBOSE_TIMING";
    private const string RequireSignedPluginsEnvVar = "EMMA_REQUIRE_SIGNED_PLUGINS";
    private const string PluginSignatureRequireSignedEnvVar = "PluginSignature__RequireSignedPlugins";
    private const string RequireSignedPluginsFileEnvVar = "EMMA_REQUIRE_SIGNED_PLUGINS_FILE";
    private const string RequireSignedPluginsManifestFileName = ".plugin-signature-require-signed";
    private const string RequireSignedPluginsAppFileName = "plugin-signature-require-signed";
    private const string PluginSignatureHmacKeyEnvVar = "EMMA_PLUGIN_SIGNATURE_HMAC_KEY_BASE64";
    private const string PluginSignatureHmacKeyConfigEnvVar = "PluginSignature__HmacKeyBase64";
    private const string PluginSignatureHmacKeyFileEnvVar = "EMMA_PLUGIN_SIGNATURE_HMAC_KEY_FILE";
    private const string PluginSignatureHmacKeyFileName = ".plugin-signature-hmac.key";
    private sealed class RuntimeState(EmbeddedRuntime runtime, InMemoryMediaStore store)
    {
        public EmbeddedRuntime Runtime { get; } = runtime;
        public InMemoryMediaStore Store { get; } = store;
        public string? SelectedPluginId { get; set; }
    }
    private sealed record PluginSummary(
        string Id,
        string Title,
        string BuildType,
        double? ThumbnailAspectRatio = null,
        string? ThumbnailFit = null,
        int? ThumbnailWidth = null,
        int? ThumbnailHeight = null,
        string? SearchExperienceJson = null);
    private sealed record PluginPathConfiguration(string? ManifestsDirectory, string? PluginsDirectory);

    private static readonly ConcurrentDictionary<int, RuntimeState> States = new();
    private static readonly Lock PluginHostInitLock = new();
    private static readonly Lock RuntimeLifecycleLock = new();
    private static readonly Lock ErrorLock = new();
    private static readonly NativeLogStore LogStore = new();
    private static int _nextHandle;
    private static int? _sharedRuntimeHandle;
    private static int _runtimeReferenceCount;
    private static PluginPathConfiguration _pluginPathConfiguration = new(null, null);
    private static bool _pluginHostInitialized = false;
    private static int _nativeLoggingConfigured = 0;

    // Don't use [ThreadStatic] - we need the error to be visible across threads for FFI
    private static string? _lastError;

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_start")]
    public static int RuntimeStart()
    {
        EnsureNativeLoggingConfigured();
        ClearLastError();
        LogInfo("runtime", "RuntimeStart requested.");

        try
        {
            EnsurePluginHostInitialized();

            lock (RuntimeLifecycleLock)
            {
                if (_sharedRuntimeHandle is { } existingHandle
                    && States.ContainsKey(existingHandle))
                {
                    _runtimeReferenceCount++;
                    LogDebug("runtime", $"Runtime reused. handle={existingHandle}, refCount={_runtimeReferenceCount}");
                    return existingHandle;
                }

                var store = new InMemoryMediaStore();
                IMediaSearchPort search = new InMemorySearchPort(store);
                IPageProviderPort pages = new InMemoryPageProvider(store);
                IPolicyEvaluator policy = new HostPolicyEvaluator();

                var runtime = EmbeddedRuntimeFactory.Create(search, pages, policy);

                var handle = Interlocked.Increment(ref _nextHandle);
                States[handle] = new RuntimeState(runtime, store);
                _sharedRuntimeHandle = handle;
                _runtimeReferenceCount = 1;
                LogInfo("runtime", $"Runtime started. handle={handle}");
                return handle;
            }
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_stop")]
    public static void RuntimeStop(int handle)
    {
        ClearLastError();
        LogInfo("runtime", $"RuntimeStop requested. handle={handle}");

        try
        {
            var shouldShutdownPluginHost = false;

            lock (RuntimeLifecycleLock)
            {
                if (!States.ContainsKey(handle))
                {
                    LogDebug("runtime", $"RuntimeStop ignored for missing handle={handle}");
                    return;
                }

                if (_sharedRuntimeHandle == handle)
                {
                    if (_runtimeReferenceCount > 0)
                    {
                        _runtimeReferenceCount--;
                    }

                    if (_runtimeReferenceCount > 0)
                    {
                        LogDebug("runtime", $"Runtime kept alive. handle={handle}, refCount={_runtimeReferenceCount}");
                        return;
                    }

                    _sharedRuntimeHandle = null;
                    _runtimeReferenceCount = 0;
                }

                States.TryRemove(handle, out _);
                shouldShutdownPluginHost = States.IsEmpty;
            }

            if (shouldShutdownPluginHost)
            {
                // Optionally shutdown plugin host when all runtimes are stopped
                ShutdownPluginHost();
            }

            LogInfo("runtime", $"Runtime stopped. handle={handle}");
        }
        catch (Exception ex)
        {
            SetLastError(ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_status")]
    public static int RuntimeStatus(int handle)
    {
        return States.ContainsKey(handle) ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_media_json")]
    public static IntPtr RuntimeListMediaJson(int handle)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var results = state.Runtime.Pipeline
                .SearchAsync(string.Empty, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var json = BuildMediaJson(results);
            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_catalog_media_json")]
    public static IntPtr RuntimeListCatalogMediaJson(int handle)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            EnsurePluginHostInitialized();
            var json = PluginHostExports.ListCatalogMediaJsonManaged();
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list catalog media.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_library_media_json")]
    public static IntPtr RuntimeListLibraryMediaJson(int handle)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            EnsurePluginHostInitialized();
            var json = PluginHostExports.ListLibraryMediaJsonManaged("Library");
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list library media.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_libraries_json")]
    public static IntPtr RuntimeListLibrariesJson(int handle)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            EnsurePluginHostInitialized();
            var json = PluginHostExports.ListLibrariesJsonManaged();
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list libraries.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_create_library")]
    public static int RuntimeCreateLibrary(int handle, IntPtr libraryNameUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var libraryName = PtrToString(libraryNameUtf8) ?? "Library";

            EnsurePluginHostInitialized();
            var created = PluginHostExports.CreateLibraryManaged(libraryName);
            if (created == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to create library.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_delete_library")]
    public static int RuntimeDeleteLibrary(int handle, IntPtr libraryNameUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var libraryName = PtrToString(libraryNameUtf8) ?? "Library";

            EnsurePluginHostInitialized();
            var deleted = PluginHostExports.DeleteLibraryManaged(libraryName);
            if (deleted == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to delete library.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_reset_database")]
    public static int RuntimeResetDatabase(int handle)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            EnsurePluginHostInitialized();
            var reset = PluginHostExports.ResetDatabaseManaged();
            if (reset == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to reset database.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_library_media_for_library_json")]
    public static IntPtr RuntimeListLibraryMediaForLibraryJson(int handle, IntPtr libraryNameUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var libraryName = PtrToString(libraryNameUtf8) ?? "Library";

            EnsurePluginHostInitialized();
            var json = PluginHostExports.ListLibraryMediaJsonManaged(libraryName);
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list library media.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_refresh_library_media_json")]
    public static IntPtr RuntimeRefreshLibraryMediaJson(int handle, IntPtr libraryNameUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var libraryName = PtrToString(libraryNameUtf8) ?? "Library";

            EnsurePluginHostInitialized();
            var json = PluginHostExports.RefreshLibraryMediaJsonManaged(libraryName);
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to refresh library media.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_plugins_json")]
    public static IntPtr RuntimeListPluginsJson()
    {
        ClearLastError();

        try
        {
            var plugins = DiscoverPlugins();
            return AllocUtf8(BuildPluginsJson(plugins));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_plugin_repositories_json")]
    public static IntPtr RuntimeListPluginRepositoriesJson()
    {
        ClearLastError();

        try
        {
            EnsurePluginHostInitialized();
            var json = PluginHostExports.ListPluginRepositoriesJsonManaged();
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list plugin repositories.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_add_plugin_repository")]
    public static int RuntimeAddPluginRepository(
        IntPtr catalogUrlUtf8,
        IntPtr repositoryIdUtf8,
        IntPtr nameUtf8,
        IntPtr sourceRepositoryUrlUtf8)
    {
        ClearLastError();

        try
        {
            EnsurePluginHostInitialized();

            var catalogUrl = PtrToString(catalogUrlUtf8);
            var repositoryId = PtrToString(repositoryIdUtf8);
            var name = PtrToString(nameUtf8);
            var sourceRepositoryUrl = PtrToString(sourceRepositoryUrlUtf8);

            var result = PluginHostExports.AddPluginRepositoryManaged(
                catalogUrl ?? string.Empty,
                repositoryId,
                name,
                sourceRepositoryUrl);

            if (result == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to add plugin repository.";
                SetLastError(error);
            }

            return result;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_remove_plugin_repository")]
    public static int RuntimeRemovePluginRepository(IntPtr repositoryIdUtf8)
    {
        ClearLastError();

        try
        {
            EnsurePluginHostInitialized();

            var repositoryId = PtrToString(repositoryIdUtf8) ?? string.Empty;
            var result = PluginHostExports.RemovePluginRepositoryManaged(repositoryId);

            if (result == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to remove plugin repository.";
                SetLastError(error);
            }

            return result;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_repository_plugins_json")]
    public static IntPtr RuntimeListRepositoryPluginsJson(IntPtr repositoryIdUtf8, int refreshCatalog)
    {
        ClearLastError();

        try
        {
            EnsurePluginHostInitialized();

            var repositoryId = PtrToString(repositoryIdUtf8) ?? string.Empty;
            var json = PluginHostExports.ListRepositoryPluginsJsonManaged(repositoryId, refreshCatalog == 1);
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list repository plugins.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_all_repository_plugins_json")]
    public static IntPtr RuntimeListAllRepositoryPluginsJson(int refreshCatalog)
    {
        ClearLastError();

        try
        {
            EnsurePluginHostInitialized();

            var json = PluginHostExports.ListAllRepositoryPluginsJsonManaged(refreshCatalog == 1);
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list repository plugins.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_install_plugin_from_repository_json")]
    public static IntPtr RuntimeInstallPluginFromRepositoryJson(
        IntPtr repositoryIdUtf8,
        IntPtr pluginIdUtf8,
        IntPtr versionUtf8,
        int refreshCatalog,
        int rescanAfterInstall)
    {
        ClearLastError();

        try
        {
            EnsurePluginHostInitialized();

            var repositoryId = PtrToString(repositoryIdUtf8) ?? string.Empty;
            var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;
            var version = PtrToString(versionUtf8);

            var json = PluginHostExports.InstallFromRepositoryJsonManaged(
                repositoryId,
                pluginId,
                version,
                refreshCatalog == 1,
                rescanAfterInstall == 1);

            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to install plugin from repository.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_configure_paths")]
    public static int RuntimeConfigurePaths(IntPtr manifestsDirUtf8, IntPtr pluginsDirUtf8)
    {
        ClearLastError();

        try
        {
            var manifestsDirectory = PtrToString(manifestsDirUtf8);
            var pluginsDirectory = PtrToString(pluginsDirUtf8);

            var previousConfiguration = _pluginPathConfiguration;

            _pluginPathConfiguration = new PluginPathConfiguration(
                NormalizeConfiguredPath(manifestsDirectory),
                NormalizeConfiguredPath(pluginsDirectory));

            if (_pluginHostInitialized)
            {
                lock (PluginHostInitLock)
                {
                    if (_pluginHostInitialized)
                    {
                        var currentManifestDirectory = ResolveManifestDirectory() ?? string.Empty;
                        var pathsChanged = !SameConfiguredPath(previousConfiguration.ManifestsDirectory, _pluginPathConfiguration.ManifestsDirectory)
                            || !SameConfiguredPath(previousConfiguration.PluginsDirectory, _pluginPathConfiguration.PluginsDirectory);
                        var signaturePolicyChanged = EnsureRequireSignedPluginsConfigured(currentManifestDirectory);
                        var signatureKeyChanged = EnsurePluginSignatureKeyConfigured(currentManifestDirectory);

                        if (pathsChanged || signaturePolicyChanged || signatureKeyChanged)
                        {
                            ShutdownPluginHost();
                            EnsurePluginHostInitialized();
                            var reasons = new List<string>();
                            if (pathsChanged)
                            {
                                reasons.Add("path change");
                            }

                            if (signaturePolicyChanged)
                            {
                                reasons.Add("signature policy change");
                            }

                            if (signatureKeyChanged)
                            {
                                reasons.Add("signature key change");
                            }

                            LogInfo("plugin-host", $"Plugin host reconfigured ({string.Join(", ", reasons)}).");
                        }
                        else
                        {
                            LogDebug("plugin-host", "Configure paths called with no effective plugin-host changes; skipping rescan.");
                        }
                    }
                }
            }

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_open_plugin")]
    public static int RuntimeOpenPlugin(int handle, IntPtr pluginIdUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var pluginId = PtrToString(pluginIdUtf8);
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                SetLastError("pluginId is required.");
                return 0;
            }

            var plugins = DiscoverPlugins();
            if (!plugins.Any(plugin => string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase)))
            {
                SetLastError($"Plugin '{pluginId}' was not found in configured manifests/plugins directories.");
                return 0;
            }

            state.SelectedPluginId = pluginId.Trim();
            LogInfo("plugin", $"Opened plugin '{state.SelectedPluginId}' for handle={handle}");

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_search_media_json")]
    public static IntPtr RuntimeSearchMediaJson(int handle, IntPtr queryUtf8)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string pluginIdForLog = "<none>";
        var queryLengthForLog = 0;
        var responseBytesForLog = 0;
        long pluginHostCallMs = 0;
        string? pluginHostTimingSnapshot = null;
        string pluginHostCorrelationId = "<none>";
        var success = false;

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var query = PtrToString(queryUtf8) ?? string.Empty;
            queryLengthForLog = query.Length;
            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            LogDebug("search", $"Search requested. handle={handle}, pluginId={activePluginId ?? "<none>"}, query='{query}'");

            if (string.IsNullOrWhiteSpace(activePluginId))
            {
                SetLastError("No active plugin selected.");
                return IntPtr.Zero;
            }

            var hostResult = SearchViaEmbeddedPluginHostTimed(activePluginId, query);
            pluginHostCallMs = hostResult.HostCallMs;
            pluginHostTimingSnapshot = hostResult.HostTimingSnapshot;
            pluginHostCorrelationId = hostResult.CorrelationId;
            responseBytesForLog = hostResult.Json.Length;

            success = true;
            LogDebug("search", $"Search completed. handle={handle}, responseBytes={responseBytesForLog}");
            return AllocUtf8(hostResult.Json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
        finally
        {
            stopwatch.Stop();
            LogTimedOperation(
                "search",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, queryLength={queryLengthForLog}, responseBytes={responseBytesForLog}, success={success}",
                forceInfo: true);

            if (ShouldLogVerboseTimingDetails() && !string.IsNullOrWhiteSpace(pluginHostTimingSnapshot))
            {
                LogInfo("timing", $"search host detail (handle={handle}, pluginId={pluginIdForLog}) {pluginHostTimingSnapshot}");
            }
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_chapters_json")]
    public static IntPtr RuntimeGetChaptersJson(int handle, IntPtr mediaIdUtf8)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string mediaIdForLog = "<unset>";
        string pluginIdForLog = "<none>";
        var success = false;

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            mediaIdForLog = mediaIdValue ?? "<null>";
            if (string.IsNullOrWhiteSpace(mediaIdValue))
            {
                SetLastError("mediaId is required.");
                return IntPtr.Zero;
            }

            IReadOnlyList<MediaChapter> chapters;
            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            if (!string.IsNullOrWhiteSpace(activePluginId))
            {
                var json = PluginHostExports.GetChaptersJsonManaged(activePluginId, mediaIdValue);
                if (json == null)
                {
                    var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host chapters call returned null";
                    throw new InvalidOperationException(error);
                }

                success = true;
                return AllocUtf8(json);
            }

            chapters = state.Runtime.Pipeline
                .GetChaptersAsync(MediaId.Create(mediaIdValue), CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            success = true;
            return AllocUtf8(BuildChaptersJson(chapters));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
        finally
        {
            stopwatch.Stop();
            LogTimedOperation(
                "get-chapters",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, mediaId={mediaIdForLog}, success={success}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_benchmark_plugin_json")]
    public static IntPtr RuntimeBenchmarkPluginJson(int handle, int iterations)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string pluginIdForLog = "<none>";
        var success = false;

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            if (string.IsNullOrWhiteSpace(activePluginId))
            {
                SetLastError("No active plugin selected.");
                return IntPtr.Zero;
            }

            var normalizedIterations = Math.Clamp(iterations, 1, 1000);
            LogDebug(
                "benchmark",
                $"Benchmark requested. handle={handle}, pluginId={activePluginId}, iterations={normalizedIterations}");

            var json = PluginHostExports.BenchmarkJsonManaged(activePluginId, normalizedIterations);
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin benchmark returned null.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            success = true;
            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
        finally
        {
            stopwatch.Stop();
            LogTimedOperation(
                "benchmark",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, iterations={Math.Clamp(iterations, 1, 1000)}, success={success}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_benchmark_network_plugin_json")]
    public static IntPtr RuntimeBenchmarkNetworkPluginJson(int handle, IntPtr queryUtf8)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string pluginIdForLog = "<none>";
        var queryForLog = string.Empty;
        var success = false;

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            if (string.IsNullOrWhiteSpace(activePluginId))
            {
                SetLastError("No active plugin selected.");
                return IntPtr.Zero;
            }

            var query = PtrToString(queryUtf8);
            var normalizedQuery = string.IsNullOrWhiteSpace(query)
                ? "one piece"
                : query.Trim();
            queryForLog = normalizedQuery;

            LogDebug(
                "benchmark-network",
                $"Network benchmark requested. handle={handle}, pluginId={activePluginId}, queryLength={normalizedQuery.Length}");

            var json = PluginHostExports.BenchmarkNetworkJsonManaged(activePluginId, normalizedQuery);
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin network benchmark returned null.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            success = true;
            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
        finally
        {
            stopwatch.Stop();
            LogTimedOperation(
                "benchmark-network",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, queryLength={queryForLog.Length}, success={success}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_page_json")]
    public static IntPtr RuntimeGetPageJson(int handle, IntPtr mediaIdUtf8, IntPtr chapterIdUtf8, int pageIndex)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string mediaIdForLog = "<unset>";
        string chapterIdForLog = "<unset>";
        string pluginIdForLog = "<none>";
        var success = false;

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            var chapterId = PtrToString(chapterIdUtf8);
            mediaIdForLog = mediaIdValue ?? "<null>";
            chapterIdForLog = chapterId ?? "<null>";
            if (string.IsNullOrWhiteSpace(mediaIdValue) || string.IsNullOrWhiteSpace(chapterId))
            {
                SetLastError("mediaId and chapterId are required.");
                return IntPtr.Zero;
            }

            if (pageIndex < 0)
            {
                SetLastError("pageIndex must be >= 0.");
                return IntPtr.Zero;
            }

            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            if (!string.IsNullOrWhiteSpace(activePluginId))
            {
                var json = PluginHostExports.GetPageJsonManaged(activePluginId, mediaIdValue, chapterId, pageIndex);
                if (json == null)
                {
                    var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host page call returned null";
                    if (IsExpectedPageProbeMiss(error))
                    {
                        SetLastErrorSilently(error);
                    }
                    else
                    {
                        SetLastError(error);
                    }
                    return IntPtr.Zero;
                }

                success = true;
                return AllocUtf8(json);
            }

            var page = state.Runtime.Pipeline
                .GetPageAsync(MediaId.Create(mediaIdValue), chapterId, pageIndex, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            success = true;
            return AllocUtf8(BuildPageJson(page));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
        finally
        {
            stopwatch.Stop();
            LogTimedOperation(
                "get-page",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, mediaId={mediaIdForLog}, chapterId={chapterIdForLog}, pageIndex={pageIndex}, success={success}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_pages_json")]
    public static IntPtr RuntimeGetPagesJson(int handle, IntPtr mediaIdUtf8, IntPtr chapterIdUtf8, int startIndex, int count)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string mediaIdForLog = "<unset>";
        string chapterIdForLog = "<unset>";
        string pluginIdForLog = "<none>";
        var success = false;

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            var chapterId = PtrToString(chapterIdUtf8);
            mediaIdForLog = mediaIdValue ?? "<null>";
            chapterIdForLog = chapterId ?? "<null>";
            if (string.IsNullOrWhiteSpace(mediaIdValue) || string.IsNullOrWhiteSpace(chapterId))
            {
                SetLastError("mediaId and chapterId are required.");
                return IntPtr.Zero;
            }

            if (startIndex < 0)
            {
                SetLastError("startIndex must be >= 0.");
                return IntPtr.Zero;
            }

            if (count <= 0)
            {
                SetLastError("count must be > 0.");
                return IntPtr.Zero;
            }

            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            if (!string.IsNullOrWhiteSpace(activePluginId))
            {
                var json = PluginHostExports.GetPagesJsonManaged(activePluginId, mediaIdValue, chapterId, startIndex, count);
                if (json == null)
                {
                    var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host pages call returned null";
                    SetLastError(error);
                    return IntPtr.Zero;
                }

                success = true;
                return AllocUtf8(json);
            }

            var pages = state.Runtime.Pipeline
                .GetPagesAsync(MediaId.Create(mediaIdValue), chapterId, startIndex, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            success = true;
            return AllocUtf8(BuildPagesJson(pages));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
        finally
        {
            stopwatch.Stop();
            LogTimedOperation(
                "get-pages",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, mediaId={mediaIdForLog}, chapterId={chapterIdForLog}, startIndex={startIndex}, count={count}, success={success}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_page_asset_json")]
    public static IntPtr RuntimeGetPageAssetJson(int handle, IntPtr mediaIdUtf8, IntPtr chapterIdUtf8, int pageIndex)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string mediaIdForLog = "<unset>";
        string chapterIdForLog = "<unset>";
        string pluginIdForLog = "<none>";
        var success = false;

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            var chapterId = PtrToString(chapterIdUtf8);
            mediaIdForLog = mediaIdValue ?? "<null>";
            chapterIdForLog = chapterId ?? "<null>";
            if (string.IsNullOrWhiteSpace(mediaIdValue) || string.IsNullOrWhiteSpace(chapterId))
            {
                SetLastError("mediaId and chapterId are required.");
                return IntPtr.Zero;
            }

            if (pageIndex < 0)
            {
                SetLastError("pageIndex must be >= 0.");
                return IntPtr.Zero;
            }

            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            if (!string.IsNullOrWhiteSpace(activePluginId))
            {
                var json = PluginHostExports.GetPageAssetJsonManaged(activePluginId, mediaIdValue, chapterId, pageIndex);
                if (json == null)
                {
                    var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host page asset call returned null";
                    SetLastError(error);
                    return IntPtr.Zero;
                }

                success = true;
                return AllocUtf8(json);
            }

            var page = state.Runtime.Pipeline
                .GetPageAsync(MediaId.Create(mediaIdValue), chapterId, pageIndex, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var asset = state.Runtime.Pipeline
                .GetPageAssetAsync(page, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            success = true;
            return AllocUtf8(BuildPageAssetJson(asset));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
        finally
        {
            stopwatch.Stop();
            LogTimedOperation(
                "get-page-asset",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, mediaId={mediaIdForLog}, chapterId={chapterIdForLog}, pageIndex={pageIndex}, success={success}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_is_media_in_library")]
    public static int RuntimeIsMediaInLibrary(int handle, IntPtr mediaIdUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaIdValue))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            EnsurePluginHostInitialized();
            var isInLibrary = PluginHostExports.IsMediaInLibraryManaged(mediaIdValue, "*");
            if (!isInLibrary)
            {
                var error = PluginHostExports.GetLastErrorManaged();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    SetLastError(error);
                    return 0;
                }
            }

            return isInLibrary ? 1 : 0;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_add_media_to_library")]
    public static int RuntimeAddMediaToLibrary(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr sourceIdUtf8,
        IntPtr titleUtf8,
        IntPtr mediaTypeUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaIdValue))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            var sourceId = PtrToString(sourceIdUtf8) ?? string.Empty;
            var title = PtrToString(titleUtf8) ?? string.Empty;
            var mediaType = PtrToString(mediaTypeUtf8) ?? "paged";

            EnsurePluginHostInitialized();
            var added = PluginHostExports.AddMediaToLibraryManaged(
                mediaIdValue,
                sourceId,
                title,
                mediaType,
                "Library");

            if (added == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to add media to library.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_add_media_to_library_v2")]
    public static int RuntimeAddMediaToLibraryV2(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr sourceIdUtf8,
        IntPtr titleUtf8,
        IntPtr mediaTypeUtf8,
        IntPtr descriptionUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaIdValue))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            var sourceId = PtrToString(sourceIdUtf8) ?? string.Empty;
            var title = PtrToString(titleUtf8) ?? string.Empty;
            var mediaType = PtrToString(mediaTypeUtf8) ?? "paged";
            var description = PtrToString(descriptionUtf8);

            EnsurePluginHostInitialized();
            var added = PluginHostExports.AddMediaToLibraryManaged(
                mediaIdValue,
                sourceId,
                title,
                mediaType,
                "Library",
                description);

            if (added == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to add media to library.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_remove_media_from_library")]
    public static int RuntimeRemoveMediaFromLibrary(int handle, IntPtr mediaIdUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaIdValue))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            EnsurePluginHostInitialized();
            var removed = PluginHostExports.RemoveMediaFromLibraryManaged(mediaIdValue, "Library");
            if (removed == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to remove media from library.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_add_media_to_library_for_library")]
    public static int RuntimeAddMediaToLibraryForLibrary(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr sourceIdUtf8,
        IntPtr titleUtf8,
        IntPtr mediaTypeUtf8,
        IntPtr libraryNameUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaIdValue))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            var sourceId = PtrToString(sourceIdUtf8) ?? string.Empty;
            var title = PtrToString(titleUtf8) ?? string.Empty;
            var mediaType = PtrToString(mediaTypeUtf8) ?? "paged";
            var libraryName = PtrToString(libraryNameUtf8) ?? "Library";

            EnsurePluginHostInitialized();
            var added = PluginHostExports.AddMediaToLibraryManaged(
                mediaIdValue,
                sourceId,
                title,
                mediaType,
                libraryName);

            if (added == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to add media to library.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_add_media_to_library_for_library_v2")]
    public static int RuntimeAddMediaToLibraryForLibraryV2(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr sourceIdUtf8,
        IntPtr titleUtf8,
        IntPtr mediaTypeUtf8,
        IntPtr libraryNameUtf8,
        IntPtr descriptionUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaIdValue))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            var sourceId = PtrToString(sourceIdUtf8) ?? string.Empty;
            var title = PtrToString(titleUtf8) ?? string.Empty;
            var mediaType = PtrToString(mediaTypeUtf8) ?? "paged";
            var libraryName = PtrToString(libraryNameUtf8) ?? "Library";
            var description = PtrToString(descriptionUtf8);

            EnsurePluginHostInitialized();
            var added = PluginHostExports.AddMediaToLibraryManaged(
                mediaIdValue,
                sourceId,
                title,
                mediaType,
                libraryName,
                description);

            if (added == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to add media to library.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_remove_media_from_library_for_library")]
    public static int RuntimeRemoveMediaFromLibraryForLibrary(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr libraryNameUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaIdValue = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaIdValue))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            var libraryName = PtrToString(libraryNameUtf8) ?? "Library";

            EnsurePluginHostInitialized();
            var removed = PluginHostExports.RemoveMediaFromLibraryManaged(mediaIdValue, libraryName);
            if (removed == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to remove media from library.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_media_progress_json")]
    public static IntPtr RuntimeGetMediaProgressJson(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr pluginIdUtf8,
        IntPtr mediaTypeUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var mediaId = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("mediaId is required.");
                return IntPtr.Zero;
            }

            var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;
            var mediaType = PtrToString(mediaTypeUtf8) ?? "paged";

            EnsurePluginHostInitialized();
            var json = PluginHostExports.GetMediaProgressJsonManaged(mediaId, pluginId, mediaType);
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to get media progress.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_set_paged_progress")]
    public static int RuntimeSetPagedProgress(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr pluginIdUtf8,
        IntPtr chapterIdUtf8,
        int pageIndex,
        int completed)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaId = PtrToString(mediaIdUtf8);
            var chapterId = PtrToString(chapterIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaId) || string.IsNullOrWhiteSpace(chapterId))
            {
                SetLastError("mediaId and chapterId are required.");
                return 0;
            }

            var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;

            EnsurePluginHostInitialized();
            var result = PluginHostExports.SetPagedProgressManaged(
                mediaId,
                pluginId,
                chapterId,
                Math.Max(0, pageIndex),
                completed != 0);

            if (result == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to set paged progress.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_set_video_progress")]
    public static int RuntimeSetVideoProgress(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr pluginIdUtf8,
        double positionSeconds,
        int completed)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaId = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;

            EnsurePluginHostInitialized();
            var result = PluginHostExports.SetVideoProgressManaged(
                mediaId,
                pluginId,
                Math.Max(0, positionSeconds),
                completed != 0);

            if (result == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to set video progress.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_read_chapter_ids_json")]
    public static IntPtr RuntimeGetReadChapterIdsJson(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr pluginIdUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var mediaId = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("mediaId is required.");
                return IntPtr.Zero;
            }

            var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;

            EnsurePluginHostInitialized();
            var json = PluginHostExports.GetReadChapterIdsJsonManaged(mediaId, pluginId);
            if (json is null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to get read chapter IDs.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_history_json")]
    public static IntPtr RuntimeGetHistoryJson(int handle, int limit)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            EnsurePluginHostInitialized();
            var json = PluginHostExports.GetHistoryJsonManaged(Math.Max(1, limit));
            if (json is null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to get history.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            return AllocUtf8(json);
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_delete_media_history")]
    public static int RuntimeDeleteMediaHistory(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr pluginIdUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            var mediaId = PtrToString(mediaIdUtf8);
            if (string.IsNullOrWhiteSpace(mediaId))
            {
                SetLastError("mediaId is required.");
                return 0;
            }

            var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;

            EnsurePluginHostInitialized();
            var result = PluginHostExports.DeleteHistoryForMediaManaged(mediaId, pluginId);
            if (result == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to delete history.";
                SetLastError(error);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_last_error")]
    public static IntPtr LastError()
    {
        lock (ErrorLock)
        {
            if (string.IsNullOrWhiteSpace(_lastError))
            {
                return IntPtr.Zero;
            }

            return AllocUtf8(_lastError);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_string_free")]
    public static void StringFree(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return;
        }

        Marshal.FreeCoTaskMem(value);
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_log_read_json")]
    public static IntPtr LogReadJson(long afterSequence, int maxItems)
    {
        try
        {
            var entries = LogStore.ReadSince(afterSequence, maxItems);
            return AllocUtf8(BuildLogsJson(entries));
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_log_latest_seq")]
    public static long LogLatestSequence()
    {
        return LogStore.LatestSequence;
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_log_set_console_enabled")]
    public static void LogSetConsoleEnabled(int enabled)
    {
        LogStore.SetConsoleEnabled(enabled != 0);
        LogInfo("logging", $"Console logging {(enabled != 0 ? "enabled" : "disabled")}");
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_log_clear")]
    public static void LogClear()
    {
        LogStore.Clear();
        LogInfo("logging", "Log store cleared.");
    }

    private static void ClearLastError()
    {
        lock (ErrorLock)
        {
            _lastError = null;
        }
    }

    private static void SetLastError(string message)
    {
        lock (ErrorLock)
        {
            _lastError = message;
        }

        LogError("error", message);
    }

    private static void SetLastError(Exception ex)
    {
        lock (ErrorLock)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
        }

        LogError("exception", $"{ex.GetType().Name}: {ex.Message}");
    }

    private static void SetLastErrorSilently(string message)
    {
        lock (ErrorLock)
        {
            _lastError = message;
        }
    }

    private static bool IsExpectedPageProbeMiss(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.StartsWith("PAGE_NOT_FOUND:", StringComparison.Ordinal)
            || string.Equals(error, "Plugin returned an invalid page content URI.", StringComparison.Ordinal);
    }

    private static IntPtr AllocUtf8(string value)
    {
        return Marshal.StringToCoTaskMemUTF8(value);
    }

    private static string? PtrToString(IntPtr value)
    {
        return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
    }

    private static string BuildMediaJson(IReadOnlyList<MediaSummary> results)
    {
        var sb = new StringBuilder();
        sb.Append('[');

        for (var i = 0; i < results.Count; i++)
        {
            var item = results[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonProperty(sb, "id", item.Id.ToString());
            sb.Append(',');
            AppendJsonProperty(sb, "source", item.SourceId);
            sb.Append(',');
            AppendJsonProperty(sb, "title", item.Title);
            sb.Append(',');
            AppendJsonProperty(sb, "mediaType", item.MediaType.ToString().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl))
            {
                sb.Append(',');
                AppendJsonProperty(sb, "thumbnailUrl", item.ThumbnailUrl!);
            }
            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                sb.Append(',');
                AppendJsonProperty(sb, "description", item.Description!);
            }
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string BuildChaptersJson(IReadOnlyList<MediaChapter> chapters)
    {
        var sb = new StringBuilder();
        sb.Append('[');

        for (var i = 0; i < chapters.Count; i++)
        {
            var item = chapters[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonProperty(sb, "id", item.ChapterId ?? string.Empty);
            sb.Append(',');
            AppendJsonNumberProperty(sb, "number", item.Number);
            sb.Append(',');
            AppendJsonProperty(sb, "title", item.Title ?? string.Empty);
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string BuildPageJson(MediaPage page)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonProperty(sb, "id", page.PageId ?? string.Empty);
        sb.Append(',');
        AppendJsonNumberProperty(sb, "index", page.Index);
        sb.Append(',');
        AppendJsonProperty(sb, "contentUri", page.ContentUri.ToString());
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildPagesJson(MediaPagesResult result)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonString(sb, "pages");
        sb.Append(':');
        sb.Append('[');

        for (var i = 0; i < result.Pages.Count; i++)
        {
            var page = result.Pages[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonProperty(sb, "id", page.PageId ?? string.Empty);
            sb.Append(',');
            AppendJsonNumberProperty(sb, "index", page.Index);
            sb.Append(',');
            AppendJsonProperty(sb, "contentUri", page.ContentUri.ToString());
            sb.Append('}');
        }

        sb.Append(']');
        sb.Append(',');
        AppendJsonString(sb, "reachedEnd");
        sb.Append(':');
        sb.Append(result.ReachedEnd ? "true" : "false");
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildPageAssetJson(MediaPageAsset asset)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonProperty(sb, "contentType", asset.ContentType ?? "application/octet-stream");
        sb.Append(',');
        AppendJsonProperty(sb, "payload", Convert.ToBase64String(asset.Payload ?? Array.Empty<byte>()));
        sb.Append(',');
        AppendJsonProperty(sb, "fetchedAtUtc", asset.FetchedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        sb.Append('}');
        return sb.ToString();
    }

    private static string? ResolveActivePluginId(RuntimeState state)
    {
        if (!string.IsNullOrWhiteSpace(state.SelectedPluginId))
        {
            return state.SelectedPluginId;
        }

        var discovered = DiscoverPlugins();
        var selected = discovered.FirstOrDefault()?.Id;
        state.SelectedPluginId = selected;
        return selected;
    }

    private readonly record struct SearchHostPhaseResult(
        string Json,
        long HostCallMs,
        string? HostTimingSnapshot,
        string CorrelationId);

    private static SearchHostPhaseResult SearchViaEmbeddedPluginHostTimed(string pluginId, string query)
    {
        EnsurePluginHostInitialized();
        var correlationId = Guid.NewGuid().ToString("n");

        var hostCallStopwatch = Stopwatch.StartNew();
        var json = PluginHostExports.SearchJsonManaged(pluginId, query, correlationId);
        hostCallStopwatch.Stop();

        var hostTimingSnapshot = PluginHostExports.TakeLastSearchTimingManaged();
        if (ShouldLogVerboseTimingDetails() && !string.IsNullOrWhiteSpace(hostTimingSnapshot))
        {
            LogInfo("timing", hostTimingSnapshot!);
        }

        if (json == null)
        {
            var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host search returned null";
            throw new InvalidOperationException(error);
        }

        return new SearchHostPhaseResult(json, hostCallStopwatch.ElapsedMilliseconds, hostTimingSnapshot, correlationId);
    }

    private static bool TryGetObjectProperty(JsonElement element, string propertyName, out JsonElement objectValue)
    {
        if (element.TryGetProperty(propertyName, out var direct)
            && direct.ValueKind == JsonValueKind.Object)
        {
            objectValue = direct;
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (!string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                || candidate.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            objectValue = candidate.Value;
            return true;
        }

        objectValue = default;
        return false;
    }

    private static void EnsurePluginHostInitialized()
    {
        if (_pluginHostInitialized)
        {
            return;
        }

        lock (PluginHostInitLock)
        {
            if (_pluginHostInitialized)
            {
                return;
            }

            var manifestDirectory = ResolveManifestDirectory() ?? string.Empty;
            var sandboxDirectory = ResolvePluginSandboxDirectory() ?? string.Empty;

            EnsureRequireSignedPluginsConfigured(manifestDirectory);
            EnsurePluginSignatureKeyConfigured(manifestDirectory);

            var resultCode = PluginHostExports.InitializeManaged(manifestDirectory, sandboxDirectory);

            if (resultCode != 0)
            {
                var error = PluginHostExports.GetLastErrorManaged()
                    ?? $"Plugin host initialization failed with code {resultCode}";
                throw new InvalidOperationException(error);
            }

            _pluginHostInitialized = true;
            LogInfo("plugin-host", "Embedded plugin host initialized.");
        }
    }

    private static bool EnsurePluginSignatureKeyConfigured(string? manifestDirectory)
    {
        var existingKey = Environment.GetEnvironmentVariable(PluginSignatureHmacKeyEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureHmacKeyConfigEnvVar);

        var resolvedKey = ResolvePluginSignatureKeyFromFiles(manifestDirectory);
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            if (string.IsNullOrWhiteSpace(existingKey))
            {
                return false;
            }

            Environment.SetEnvironmentVariable(PluginSignatureHmacKeyEnvVar, null);
            Environment.SetEnvironmentVariable(PluginSignatureHmacKeyConfigEnvVar, null);
            LogInfo("plugin-host", "Cleared plugin signature key because no key file is present.");
            return true;
        }

        if (string.Equals(existingKey?.Trim(), resolvedKey, StringComparison.Ordinal))
        {
            return false;
        }

        Environment.SetEnvironmentVariable(PluginSignatureHmacKeyEnvVar, resolvedKey);
        Environment.SetEnvironmentVariable(PluginSignatureHmacKeyConfigEnvVar, resolvedKey);
        LogInfo("plugin-host", "Loaded plugin signature key from configured key file.");
        return true;
    }

    private static bool EnsureRequireSignedPluginsConfigured(string? manifestDirectory)
    {
        var existing = Environment.GetEnvironmentVariable(RequireSignedPluginsEnvVar)
            ?? Environment.GetEnvironmentVariable(PluginSignatureRequireSignedEnvVar);

        var resolved = ResolveRequireSignedPluginsFromFiles(manifestDirectory);
        if (!resolved.HasValue)
        {
            return false;
        }

        var normalized = resolved.Value ? "true" : "false";
        if (string.Equals(existing?.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Environment.SetEnvironmentVariable(RequireSignedPluginsEnvVar, normalized);
        Environment.SetEnvironmentVariable(PluginSignatureRequireSignedEnvVar, normalized);
        LogInfo("plugin-host", $"Loaded require-signed-plugins policy from file: {normalized}.");
        return true;
    }

    private static bool? ResolveRequireSignedPluginsFromFiles(string? manifestDirectory)
    {
        var candidatePaths = new List<string>();

        var explicitPath = Environment.GetEnvironmentVariable(RequireSignedPluginsFileEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidatePaths.Add(explicitPath);
        }

        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            candidatePaths.Add(Path.Combine(manifestDirectory, RequireSignedPluginsManifestFileName));
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            candidatePaths.Add(Path.Combine(support, "emmaui", RequireSignedPluginsAppFileName));
        }

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var value = File.ReadAllText(path).Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (TryParseBooleanLike(value, out var parsed))
                {
                    return parsed;
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static bool TryParseBooleanLike(string value, out bool parsed)
    {
        if (bool.TryParse(value, out parsed))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "yes":
            case "on":
                parsed = true;
                return true;
            case "0":
            case "no":
            case "off":
                parsed = false;
                return true;
            default:
                parsed = false;
                return false;
        }
    }

    private static string? ResolvePluginSignatureKeyFromFiles(string? manifestDirectory)
    {
        var candidatePaths = new List<string>();

        var explicitPath = Environment.GetEnvironmentVariable(PluginSignatureHmacKeyFileEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidatePaths.Add(explicitPath);
        }

        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            candidatePaths.Add(Path.Combine(manifestDirectory, PluginSignatureHmacKeyFileName));
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            candidatePaths.Add(Path.Combine(support, "emmaui", "plugin-signature-hmac.key"));
        }

        foreach (var path in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var value = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }

    private static void ShutdownPluginHost()
    {
        lock (PluginHostInitLock)
        {
            if (!_pluginHostInitialized)
            {
                return;
            }

            PluginHostExports.ShutdownManaged();
            _pluginHostInitialized = false;
            LogInfo("plugin-host", "Embedded plugin host shutdown.");
        }
    }

    private static string BuildLogsJson(IReadOnlyList<NativeLogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append('[');

        for (var i = 0; i < entries.Count; i++)
        {
            var item = entries[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonNumberProperty(sb, "seq", item.Sequence);
            sb.Append(',');
            AppendJsonProperty(sb, "ts", item.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            sb.Append(',');
            AppendJsonProperty(sb, "level", item.Level.ToString());
            sb.Append(',');
            AppendJsonProperty(sb, "category", item.Category);
            sb.Append(',');
            AppendJsonProperty(sb, "message", item.Message);
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void LogDebug(string category, string message)
    {
        LogStore.Write(NativeLogLevel.Debug, category, message);
    }

    private static void LogInfo(string category, string message)
    {
        LogStore.Write(NativeLogLevel.Information, category, message);
    }

    private static void LogError(string category, string message)
    {
        LogStore.Write(NativeLogLevel.Error, category, message);
    }

    private static void LogTimedOperation(string operation, long elapsedMs, string details, bool forceInfo = false)
    {
        var message = $"{operation} took {elapsedMs}ms ({details})";
        if (forceInfo || elapsedMs >= 500)
        {
            LogInfo("timing", message);
            return;
        }

        LogDebug("timing", message);
    }

    private static bool ShouldLogVerboseTimingDetails()
    {
        var value = Environment.GetEnvironmentVariable(NativeVerboseTimingEnvVar)
            ?? Environment.GetEnvironmentVariable("EMMA_WASM_PAYLOAD_DIAGNOSTICS")
            ?? Environment.GetEnvironmentVariable("EMMA_PLUGIN_TIMING_DIAGNOSTICS");

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

    private static void EnsureNativeLoggingConfigured()
    {
        if (Interlocked.Exchange(ref _nativeLoggingConfigured, 1) != 0)
        {
            return;
        }

        var configuredLevel = ResolveNativeConsoleLogLevel();
        LogStore.SetConsoleMinLevel(configuredLevel);
    }

    private static NativeLogLevel ResolveNativeConsoleLogLevel()
    {
        var value = Environment.GetEnvironmentVariable(NativeConsoleLogLevelEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            return NativeLogLevel.Information;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "trace" => NativeLogLevel.Trace,
            "debug" => NativeLogLevel.Debug,
            "information" or "info" => NativeLogLevel.Information,
            "warning" or "warn" => NativeLogLevel.Warning,
            "error" => NativeLogLevel.Error,
            _ => NativeLogLevel.Information
        };
    }

    private static string? ResolveManifestDirectory()
    {
        var explicitManifestDir = Environment.GetEnvironmentVariable("EMMA_MANIFESTS_DIR");
        if (!string.IsNullOrWhiteSpace(explicitManifestDir))
        {
            return explicitManifestDir;
        }

        if (!string.IsNullOrWhiteSpace(_pluginPathConfiguration.ManifestsDirectory))
        {
            return _pluginPathConfiguration.ManifestsDirectory;
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            return Path.Combine(support, "emmaui", "manifests");
        }

        return null;
    }

    private static string? ResolvePluginSandboxDirectory()
    {
        var explicitPluginsDir = Environment.GetEnvironmentVariable("EMMA_PLUGINS_DIR");
        if (!string.IsNullOrWhiteSpace(explicitPluginsDir))
        {
            return explicitPluginsDir;
        }

        if (!string.IsNullOrWhiteSpace(_pluginPathConfiguration.PluginsDirectory))
        {
            return _pluginPathConfiguration.PluginsDirectory;
        }

        var support = GetApplicationSupportDirectories().FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(support))
        {
            return Path.Combine(support, "emmaui", "plugins");
        }

        return null;
    }

    private static string BuildPluginsJson(IReadOnlyList<PluginSummary> plugins)
    {
        var sb = new StringBuilder();
        sb.Append('[');

        for (var i = 0; i < plugins.Count; i++)
        {
            var plugin = plugins[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonProperty(sb, "id", plugin.Id);
            sb.Append(',');
            AppendJsonProperty(sb, "title", plugin.Title);
            sb.Append(',');
            AppendJsonProperty(sb, "buildType", plugin.BuildType);
            if (plugin.ThumbnailAspectRatio is { } aspectRatio && aspectRatio > 0)
            {
                sb.Append(',');
                AppendJsonDoubleProperty(sb, "thumbnailAspectRatio", aspectRatio);
            }

            if (!string.IsNullOrWhiteSpace(plugin.ThumbnailFit))
            {
                sb.Append(',');
                AppendJsonProperty(sb, "thumbnailFit", plugin.ThumbnailFit!);
            }

            if (plugin.ThumbnailWidth is { } width && width > 0)
            {
                sb.Append(',');
                AppendJsonNumberProperty(sb, "thumbnailWidth", width);
            }

            if (plugin.ThumbnailHeight is { } height && height > 0)
            {
                sb.Append(',');
                AppendJsonNumberProperty(sb, "thumbnailHeight", height);
            }

            if (!string.IsNullOrWhiteSpace(plugin.SearchExperienceJson))
            {
                sb.Append(',');
                AppendJsonString(sb, "searchExperience");
                sb.Append(':');
                sb.Append(plugin.SearchExperienceJson);
            }
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static IReadOnlyList<PluginSummary> DiscoverPlugins()
    {
        var byId = new Dictionary<string, PluginSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestDirectory in GetManifestDirectories())
        {
            if (!Directory.Exists(manifestDirectory))
            {
                continue;
            }

            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(manifestDirectory, "*.plugin.json", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var manifestPath in manifests)
            {
                var summary = TryParseManifest(manifestPath);
                if (summary is null)
                {
                    continue;
                }

                var buildType = ResolvePluginBuildType(summary.Id);
                byId[summary.Id] = summary with { BuildType = buildType };
            }
        }

        foreach (var pluginsDirectory in GetPluginDirectories())
        {
            if (!Directory.Exists(pluginsDirectory))
            {
                continue;
            }

            IEnumerable<string> pluginDirectories;
            try
            {
                pluginDirectories = Directory.EnumerateDirectories(pluginsDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var pluginDirectory in pluginDirectories)
            {
                var id = Path.GetFileName(pluginDirectory);
                if (string.IsNullOrWhiteSpace(id) || byId.ContainsKey(id))
                {
                    continue;
                }

                byId[id] = new PluginSummary(id, id, ResolvePluginBuildType(id));
            }
        }

        return byId.Values
            .OrderBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PluginSummary? TryParseManifest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            var root = document.RootElement;
            var id = GetStringProperty(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var title = GetStringProperty(root, "name")
                        ?? GetStringProperty(root, "title")
                        ?? id;

            double? thumbnailAspectRatio = null;
            string? thumbnailFit = null;
            int? thumbnailWidth = null;
            int? thumbnailHeight = null;
            string? searchExperienceJson = null;

            if (TryGetObjectProperty(root, "thumbnail", out var thumbnail))
            {
                thumbnailAspectRatio = GetDoubleProperty(thumbnail, "aspectRatio");
                thumbnailFit = GetStringProperty(thumbnail, "fit");
                thumbnailWidth = GetInt32Property(thumbnail, "width");
                thumbnailHeight = GetInt32Property(thumbnail, "height");

                if ((thumbnailAspectRatio is null || thumbnailAspectRatio <= 0)
                    && thumbnailWidth is { } width
                    && thumbnailHeight is { } height
                    && width > 0
                    && height > 0)
                {
                    thumbnailAspectRatio = (double)width / height;
                }
            }

            if (root.TryGetProperty("searchExperience", out var searchExperience)
                && searchExperience.ValueKind == JsonValueKind.Object)
            {
                searchExperienceJson = searchExperience.GetRawText();
            }

            return new PluginSummary(id, title, "csharp", thumbnailAspectRatio, thumbnailFit, thumbnailWidth, thumbnailHeight, searchExperienceJson);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolvePluginBuildType(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return "csharp";
        }

        foreach (var pluginsDirectory in GetPluginDirectories())
        {
            if (string.IsNullOrWhiteSpace(pluginsDirectory) || !Directory.Exists(pluginsDirectory))
            {
                continue;
            }

            var pluginRoot = Path.Combine(pluginsDirectory, pluginId);
            if (!Directory.Exists(pluginRoot))
            {
                continue;
            }

            if (ContainsComponentWasmArtifacts(pluginRoot))
            {
                return "cwasm";
            }

            if (ContainsCoreWasmArtifacts(pluginRoot))
            {
                return "wasm";
            }
        }

        return "csharp";
    }

    private static bool ContainsComponentWasmArtifacts(string pluginRoot)
    {
        static bool Exists(string path) => File.Exists(path);

        if (Exists(Path.Combine(pluginRoot, "plugin.cwasm"))
            || Exists(Path.Combine(pluginRoot, "wasm", "plugin.cwasm")))
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(pluginRoot, "*.cwasm", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsCoreWasmArtifacts(string pluginRoot)
    {
        static bool Exists(string path) => File.Exists(path);

        if (Exists(Path.Combine(pluginRoot, "plugin.wasm"))
            || Exists(Path.Combine(pluginRoot, "wasm", "plugin.wasm")))
        {
            return true;
        }

        try
        {
            return Directory.EnumerateFiles(pluginRoot, "*.wasm", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            var directValue = property.GetString();
            return string.IsNullOrWhiteSpace(directValue) ? null : directValue.Trim();
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (!string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                || candidate.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var matchedValue = candidate.Value.GetString();
            return string.IsNullOrWhiteSpace(matchedValue) ? null : matchedValue.Trim();
        }

        return null;
    }

    private static int? GetInt32Property(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? GetDoubleProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numeric))
        {
            return numeric;
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IEnumerable<string> GetManifestDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfNotEmpty(set, _pluginPathConfiguration.ManifestsDirectory);

        AddIfNotEmpty(set, Environment.GetEnvironmentVariable("EMMA_MANIFESTS_DIR"));
        AddIfNotEmpty(set, Path.Combine(Environment.CurrentDirectory, "manifests"));
        AddIfNotEmpty(set, Path.Combine(AppContext.BaseDirectory, "manifests"));

        foreach (var supportDirectory in GetApplicationSupportDirectories())
        {
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "manifests"));
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "emmaui", "manifests"));
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "com.example.emmaui", "emmaui", "manifests"));
        }

        return set;
    }

    private static IEnumerable<string> GetPluginDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfNotEmpty(set, _pluginPathConfiguration.PluginsDirectory);

        AddIfNotEmpty(set, Environment.GetEnvironmentVariable("EMMA_PLUGINS_DIR"));
        AddIfNotEmpty(set, Path.Combine(Environment.CurrentDirectory, "plugins"));
        AddIfNotEmpty(set, Path.Combine(AppContext.BaseDirectory, "plugins"));

        foreach (var supportDirectory in GetApplicationSupportDirectories())
        {
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "plugins"));
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "emmaui", "plugins"));
            AddIfNotEmpty(set, Path.Combine(supportDirectory, "com.example.emmaui", "emmaui", "plugins"));
        }

        return set;
    }

    private static IEnumerable<string> GetApplicationSupportDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfNotEmpty(set, Environment.GetEnvironmentVariable("EMMA_APP_SUPPORT_DIR"));

        if (OperatingSystem.IsLinux())
        {
            AddIfNotEmpty(set, Environment.GetEnvironmentVariable("XDG_DATA_HOME"));

            var homeEnv = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(homeEnv))
            {
                AddIfNotEmpty(set, Path.Combine(homeEnv, ".local", "share"));
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(home))
        {
            AddIfNotEmpty(set, Path.Combine(home, "Library", "Application Support"));
        }

        return set;
    }

    private static void AddIfNotEmpty(ISet<string> set, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        set.Add(path);
    }

    private static string? NormalizeConfiguredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string? NormalizeConfiguredValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static bool SameConfiguredPath(string? left, string? right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(left, right, comparison);
    }

    private static void AppendJsonProperty(StringBuilder sb, string name, string value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        AppendJsonString(sb, value);
    }

    private static void AppendJsonNumberProperty(StringBuilder sb, string name, long value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendJsonDoubleProperty(StringBuilder sb, string name, double value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
