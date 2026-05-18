using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using EMMA.Domain;

namespace EMMA.Native;

public static partial class NativeExports
{
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

            if (item.Metadata is { Count: > 0 })
            {
                sb.Append(',');
                sb.Append('"');
                sb.Append("metadata");
                sb.Append('"');
                sb.Append(':');
                sb.Append('[');

                var metadataIndex = 0;
                foreach (var metadata in item.Metadata)
                {
                    if (metadataIndex > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append('{');
                    AppendJsonProperty(sb, "key", metadata.Key);
                    sb.Append(',');
                    AppendJsonProperty(sb, "value", metadata.Value);
                    sb.Append('}');
                    metadataIndex++;
                }

                sb.Append(']');
            }

            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
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
}