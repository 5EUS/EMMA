using System.Runtime.InteropServices;

using EMMA.PluginHost.Library;

namespace EMMA.Native;

public static partial class NativeExports
{
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_add_media_to_library_v3")]
    public static int RuntimeAddMediaToLibraryV3(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr sourceIdUtf8,
        IntPtr titleUtf8,
        IntPtr mediaTypeUtf8,
        IntPtr descriptionUtf8,
        IntPtr thumbnailUrlUtf8)
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
            var thumbnailUrl = PtrToString(thumbnailUrlUtf8);

            EnsurePluginHostInitialized();
            var added = PluginHostExports.AddMediaToLibraryManaged(
                mediaIdValue,
                sourceId,
                title,
                mediaType,
                "Library",
                description,
                thumbnailUrl);

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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_add_media_to_library_for_library_v3")]
    public static int RuntimeAddMediaToLibraryForLibraryV3(
        int handle,
        IntPtr mediaIdUtf8,
        IntPtr sourceIdUtf8,
        IntPtr titleUtf8,
        IntPtr mediaTypeUtf8,
        IntPtr libraryNameUtf8,
        IntPtr descriptionUtf8,
        IntPtr thumbnailUrlUtf8)
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
            var thumbnailUrl = PtrToString(thumbnailUrlUtf8);

            EnsurePluginHostInitialized();
            var added = PluginHostExports.AddMediaToLibraryManaged(
                mediaIdValue,
                sourceId,
                title,
                mediaType,
                libraryName,
                description,
                thumbnailUrl);

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
}