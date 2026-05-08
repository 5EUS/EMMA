using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using EMMA.Domain;
using EMMA.PluginHost.Library;

namespace EMMA.Native;

public static partial class NativeExports
{
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

            if (!TryGetRemotePluginHostBaseUri(out _))
            {
                EnsurePluginHostInitialized();

                var openResult = PluginHostExports.OpenPluginManaged(pluginId.Trim());
                if (openResult == 0)
                {
                    var error = PluginHostExports.GetLastErrorManaged() ?? $"Plugin '{pluginId}' could not be opened.";
                    SetLastError(DecorateOpenPluginError(error));
                    return 0;
                }
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
        string? pluginHostTimingSnapshot = null;
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

            var hostResult = TryGetRemotePluginHostBaseUri(out var remoteBaseUri)
                ? SearchViaRemotePluginHostTimed(remoteBaseUri, activePluginId, query)
                : SearchViaEmbeddedPluginHostTimed(activePluginId, query);
            pluginHostTimingSnapshot = hostResult.HostTimingSnapshot;
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_enrich_media_json")]
    public static IntPtr RuntimeEnrichMediaJson(int handle, IntPtr mediaJsonUtf8)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string pluginIdForLog = "<none>";
        var responseBytesForLog = 0;
        var success = false;

        try
        {
            if (!States.TryGetValue(handle, out var state))
            {
                SetLastError("Runtime handle not found.");
                return IntPtr.Zero;
            }

            var mediaJson = PtrToString(mediaJsonUtf8) ?? string.Empty;
            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";

            if (string.IsNullOrWhiteSpace(activePluginId))
            {
                SetLastError("No active plugin selected.");
                return IntPtr.Zero;
            }

            EnsurePluginHostInitialized();
            var enrichedJson = PluginHostExports.EnrichMediaJsonManaged(activePluginId, mediaJson);
            if (enrichedJson is null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host enrichment returned null.";
                SetLastError(error);
                return IntPtr.Zero;
            }

            responseBytesForLog = Encoding.UTF8.GetByteCount(enrichedJson);
            success = true;
            return AllocUtf8(enrichedJson);
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
                "enrich-media",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, responseBytes={responseBytesForLog}, success={success}",
                forceInfo: true);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_video_streams_json")]
    public static IntPtr RuntimeGetVideoStreamsJson(int handle, IntPtr mediaIdUtf8)
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

            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            if (string.IsNullOrWhiteSpace(activePluginId))
            {
                SetLastError("No active plugin selected.");
                return IntPtr.Zero;
            }

            var json = PluginHostExports.GetVideoStreamsJsonManaged(activePluginId, mediaIdValue);
            if (json is null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host video streams call returned null";
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
                "get-video-streams",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, mediaId={mediaIdForLog}, success={success}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_get_video_segment_json")]
    public static IntPtr RuntimeGetVideoSegmentJson(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr streamIdUtf8,
        int sequence)
    {
        ClearLastError();
        var stopwatch = Stopwatch.StartNew();
        string mediaIdForLog = "<unset>";
        string streamIdForLog = "<unset>";
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
            var streamId = PtrToString(streamIdUtf8);
            mediaIdForLog = mediaIdValue ?? "<null>";
            streamIdForLog = streamId ?? "<null>";
            if (string.IsNullOrWhiteSpace(mediaIdValue) || string.IsNullOrWhiteSpace(streamId))
            {
                SetLastError("mediaId and streamId are required.");
                return IntPtr.Zero;
            }

            if (sequence < 0)
            {
                SetLastError("sequence must be >= 0.");
                return IntPtr.Zero;
            }

            var activePluginId = ResolveActivePluginId(state);
            pluginIdForLog = activePluginId ?? "<none>";
            if (string.IsNullOrWhiteSpace(activePluginId))
            {
                SetLastError("No active plugin selected.");
                return IntPtr.Zero;
            }

            var json = PluginHostExports.GetVideoSegmentJsonManaged(activePluginId, mediaIdValue, streamId, sequence);
            if (json is null)
            {
                var error = PluginHostExports.GetLastErrorManaged() ?? "Plugin host video segment call returned null";
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
                "get-video-segment",
                stopwatch.ElapsedMilliseconds,
                $"handle={handle}, pluginId={pluginIdForLog}, mediaId={mediaIdForLog}, streamId={streamIdForLog}, sequence={sequence}, success={success}");
        }
    }
}