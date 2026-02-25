using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using EMMA.Api;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.Infrastructure.InMemory;
using EMMA.Infrastructure.Policy;

namespace EMMA.Native;

public static class NativeExports
{
    private sealed record RuntimeState(EmbeddedRuntime Runtime, InMemoryMediaStore Store);

    private static readonly ConcurrentDictionary<int, RuntimeState> States = new();
    private static int _nextHandle;

    [ThreadStatic]
    private static string? _lastError;

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_start")]
    public static int RuntimeStart()
    {
        ClearLastError();

        try
        {
            var store = new InMemoryMediaStore();
            IMediaSearchPort search = new InMemorySearchPort(store);
            IPageProviderPort pages = new InMemoryPageProvider(store);
            IPolicyEvaluator policy = new HostPolicyEvaluator();

            var runtime = EmbeddedRuntimeFactory.Create(search, pages, policy);

            var handle = Interlocked.Increment(ref _nextHandle);
            States[handle] = new RuntimeState(runtime, store);
            return handle;
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

        try
        {
            States.TryRemove(handle, out _);
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

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_list_plugins_json")]
    public static IntPtr RuntimeListPluginsJson()
    {
        ClearLastError();

        try
        {
            return AllocUtf8("[]");
        }
        catch (Exception ex)
        {
            SetLastError(ex);
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_open_plugin")]
    public static int RuntimeOpenPlugin(int handle, IntPtr pluginIdUtf8)
    {
        ClearLastError();

        try
        {
            if (!States.ContainsKey(handle))
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

            SetLastError("Plugin open is not implemented for the embedded runtime.");
            return 0;
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
        if (string.IsNullOrWhiteSpace(_lastError))
        {
            return IntPtr.Zero;
        }

        return AllocUtf8(_lastError);
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

    private static void ClearLastError()
    {
        _lastError = null;
    }

    private static void SetLastError(string message)
    {
        _lastError = message;
    }

    private static void SetLastError(Exception ex)
    {
        _lastError = ex.Message;
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
            sb.Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static void AppendJsonProperty(StringBuilder sb, string name, string value)
    {
        AppendJsonString(sb, name);
        sb.Append(':');
        AppendJsonString(sb, value);
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
