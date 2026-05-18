using System.Net.Http;
using System.Runtime.InteropServices;

using EMMA.PluginHost.Library;

namespace EMMA.Native;

public static partial class NativeExports
{
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

            return AllocUtf8(BuildMediaJson(results));
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

            if (TryGetRemotePluginHostBaseUri(out _))
            {
                return AllocUtf8("[]");
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

            if (TryGetRemotePluginHostBaseUri(out _))
            {
                SetLastError("Create-library is not available when remote plugin host mode is enabled.");
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

            if (TryGetRemotePluginHostBaseUri(out _))
            {
                SetLastError("Delete-library is not available when remote plugin host mode is enabled.");
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

            if (TryGetRemotePluginHostBaseUri(out _))
            {
                SetLastError("Reset-database is not available when remote plugin host mode is enabled.");
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

            if (TryGetRemotePluginHostBaseUri(out _))
            {
                return AllocUtf8("[]");
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

            if (TryGetRemotePluginHostBaseUri(out _))
            {
                return AllocUtf8("[]");
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
            if (TryGetRemotePluginHostBaseUri(out var remoteBaseUri))
            {
                return AllocUtf8(NormalizeRemotePluginsPayload(HttpGetJson(remoteBaseUri, "/plugins/available")));
            }

            EnsurePluginHostInitialized();
            var json = PluginHostExports.ListPluginsJsonManaged();
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list plugins.";
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_plugin_repositories_json")]
    public static IntPtr RuntimeListPluginRepositoriesJson()
    {
        ClearLastError();

        try
        {
            if (TryGetRemotePluginHostBaseUri(out var remoteBaseUri))
            {
                return AllocUtf8(HttpGetJson(remoteBaseUri, "/plugins/repositories"));
            }

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
            var catalogUrl = PtrToString(catalogUrlUtf8);
            var repositoryId = PtrToString(repositoryIdUtf8);
            var name = PtrToString(nameUtf8);
            var sourceRepositoryUrl = PtrToString(sourceRepositoryUrlUtf8);

            if (TryGetRemotePluginHostBaseUri(out var remoteBaseUri))
            {
                var payload = BuildAddPluginRepositoryPayload(
                    catalogUrl ?? string.Empty,
                    repositoryId,
                    name,
                    sourceRepositoryUrl);

                HttpSendJson(remoteBaseUri, "/plugins/repositories", HttpMethod.Post, payload);
                return 1;
            }

            EnsurePluginHostInitialized();

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
            var repositoryId = PtrToString(repositoryIdUtf8) ?? string.Empty;

            if (TryGetRemotePluginHostBaseUri(out var remoteBaseUri))
            {
                HttpSendJson(remoteBaseUri, $"/plugins/repositories/{Uri.EscapeDataString(repositoryId)}", HttpMethod.Delete, null);
                return 1;
            }

            EnsurePluginHostInitialized();
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
            var repositoryId = PtrToString(repositoryIdUtf8) ?? string.Empty;

            if (TryGetRemotePluginHostBaseUri(out var remoteBaseUri))
            {
                var refreshValue = refreshCatalog == 1 ? "true" : "false";
                var remoteJson = HttpGetJson(
                    remoteBaseUri,
                    $"/plugins/repositories/{Uri.EscapeDataString(repositoryId)}/plugins?refresh={refreshValue}");
                return AllocUtf8(remoteJson);
            }

            EnsurePluginHostInitialized();
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
            if (TryGetRemotePluginHostBaseUri(out var remoteBaseUri))
            {
                var refreshValue = refreshCatalog == 1 ? "true" : "false";
                return AllocUtf8(HttpGetJson(remoteBaseUri, $"/plugins/repository-plugins?refresh={refreshValue}"));
            }

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
            var repositoryId = PtrToString(repositoryIdUtf8) ?? string.Empty;
            var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;
            var version = PtrToString(versionUtf8);

            if (TryGetRemotePluginHostBaseUri(out var remoteBaseUri))
            {
                var payload = BuildInstallFromRepositoryPayload(
                    repositoryId,
                    pluginId,
                    version,
                    refreshCatalog == 1,
                    rescanAfterInstall == 1);

                var remoteJson = HttpSendJson(
                    remoteBaseUri,
                    "/plugins/install-from-repository",
                    HttpMethod.Post,
                    payload);
                return AllocUtf8(remoteJson);
            }

            EnsurePluginHostInitialized();

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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_enqueue_download_json")]
    public static IntPtr RuntimeEnqueueDownloadJson(
        int handle,
        IntPtr pluginIdUtf8,
        IntPtr mediaIdUtf8,
        IntPtr mediaTypeUtf8,
        IntPtr chapterIdUtf8,
        IntPtr streamIdUtf8)
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

            var pluginId = PtrToString(pluginIdUtf8) ?? string.Empty;
            var mediaId = PtrToString(mediaIdUtf8) ?? string.Empty;
            var mediaType = PtrToString(mediaTypeUtf8) ?? "paged";
            var chapterId = PtrToString(chapterIdUtf8);
            var streamId = PtrToString(streamIdUtf8);

            var json = PluginHostExports.EnqueueDownloadJsonManaged(
                pluginId,
                mediaId,
                mediaType,
                chapterId,
                streamId);

            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to enqueue download.";
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_downloads_json")]
    public static IntPtr RuntimeListDownloadsJson(int handle, int limit)
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
            var json = PluginHostExports.ListDownloadsJsonManaged(Math.Max(1, limit));
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to list downloads.";
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_download_json")]
    public static IntPtr RuntimeGetDownloadJson(int handle, IntPtr jobIdUtf8)
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
            var jobId = PtrToString(jobIdUtf8) ?? string.Empty;
            var json = PluginHostExports.GetDownloadJsonManaged(jobId);
            if (json == null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Failed to get download.";
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_pause_download")]
    public static int RuntimePauseDownload(int handle, IntPtr jobIdUtf8)
    {
        return RuntimeChangeDownloadState(handle, jobIdUtf8, PluginHostExports.PauseDownloadManaged, "pause");
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_resume_download")]
    public static int RuntimeResumeDownload(int handle, IntPtr jobIdUtf8)
    {
        return RuntimeChangeDownloadState(handle, jobIdUtf8, PluginHostExports.ResumeDownloadManaged, "resume");
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_retry_download")]
    public static int RuntimeRetryDownload(int handle, IntPtr jobIdUtf8)
    {
        return RuntimeChangeDownloadState(handle, jobIdUtf8, PluginHostExports.RetryDownloadManaged, "retry");
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_cancel_download")]
    public static int RuntimeCancelDownload(int handle, IntPtr jobIdUtf8)
    {
        return RuntimeChangeDownloadState(handle, jobIdUtf8, PluginHostExports.CancelDownloadManaged, "cancel");
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_delete_download")]
    public static int RuntimeDeleteDownload(int handle, IntPtr jobIdUtf8)
    {
        return RuntimeChangeDownloadState(handle, jobIdUtf8, PluginHostExports.DeleteDownloadManaged, "delete");
    }

    private static int RuntimeChangeDownloadState(
        int handle,
        IntPtr jobIdUtf8,
        Func<string, int> action,
        string operation)
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
            var jobId = PtrToString(jobIdUtf8) ?? string.Empty;
            var result = action(jobId);
            if (result == 0)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? $"Failed to {operation} download.";
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
                        var signatureKeyChanged = EnsurePluginSignatureTrustConfigured(currentManifestDirectory);

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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_configure_host")]
    public static int RuntimeConfigureHost(IntPtr executablePathUtf8, IntPtr baseUrlUtf8, IntPtr modeUtf8)
    {
        ClearLastError();

        try
        {
            var executablePath = NormalizeConfiguredPath(PtrToString(executablePathUtf8));
            var baseUrlRaw = PtrToString(baseUrlUtf8)?.Trim();
            var modeRaw = PtrToString(modeUtf8)?.Trim();

            var normalizedMode = string.Equals(modeRaw, "remote", StringComparison.OrdinalIgnoreCase)
                ? "remote"
                : "local";

            string? normalizedBaseUrl = null;
            if (normalizedMode == "remote")
            {
                if (string.IsNullOrWhiteSpace(baseUrlRaw))
                {
                    SetLastError("Remote host mode requires a baseUrl.");
                    return 0;
                }

                if (!Uri.TryCreate(baseUrlRaw, UriKind.Absolute, out var baseUri))
                {
                    SetLastError("Remote host baseUrl must be an absolute URL.");
                    return 0;
                }

                normalizedBaseUrl = baseUri.ToString().TrimEnd('/');
            }

            var previous = _pluginHostConfiguration;
            var updated = new PluginHostConfiguration(normalizedMode, normalizedBaseUrl, executablePath);
            _pluginHostConfiguration = updated;

            if (normalizedMode == "remote" && _pluginHostInitialized)
            {
                ShutdownPluginHost();
            }

            if (!string.Equals(previous.Mode, updated.Mode, StringComparison.Ordinal)
                || !string.Equals(previous.BaseUrl, updated.BaseUrl, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.ExecutablePath, updated.ExecutablePath, StringComparison.Ordinal))
            {
                LogInfo("plugin-host", $"Plugin host mode configured: mode={updated.Mode}, baseUrl={updated.BaseUrl ?? "<none>"}");
            }

            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_set_max_concurrent_downloads")]
    public static int RuntimeSetMaxConcurrentDownloads(int handle, int maxConcurrentDownloads)
    {
        ClearLastError();

        try
        {
            if (!States.TryGetValue(handle, out _))
            {
                SetLastError("Runtime handle not found.");
                return 0;
            }

            if (TryGetRemotePluginHostBaseUri(out _))
            {
                SetLastError("Download concurrency can only be configured for the embedded local plugin host.");
                return 0;
            }

            if (!PluginHostExports.SetMaxConcurrentDownloadsManaged(maxConcurrentDownloads))
            {
                SetLastError(PluginHostExports.GetLastErrorManaged() ?? "Failed to configure download concurrency.");
                return 0;
            }

            LogInfo(
                "plugin-host",
                $"Configured embedded download concurrency to {PluginHostExports.GetMaxConcurrentDownloadsManaged()}.");
            return 1;
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return 0;
        }
    }
}