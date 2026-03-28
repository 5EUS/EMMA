using System.Runtime.InteropServices;
using System.Text.Json;
using System.Collections.Concurrent;
using EMMA.PluginHost.Configuration;
using EMMA.PluginHost.Platform;
using Microsoft.Extensions.Options;

namespace EMMA.PluginHost.Services;

public sealed class NativeInProcessWasmComponentInvoker : IWasmComponentInvoker, IDisposable
{
    private const int NativeSuccess = 0;
    private const uint DefaultTimeoutMs = 30_000u;
    private const uint SearchTimeoutMs = 30_000u;
    private static readonly Lock LastTimingLock = new();
    private static PluginHostOptions _pluginHostOptions = new();
    private readonly ConcurrentDictionary<string, CachedPluginHandle> _pluginHandles = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private readonly record struct CachedPluginHandle(
        ulong Handle,
        DateTime LastWriteTimeUtc,
        long Length);

    public NativeInProcessWasmComponentInvoker(IOptions<PluginHostOptions> options)
    {
        _pluginHostOptions = options.Value;
    }

    public Task<string> InvokeAsync(
        string componentPath,
        string operation,
        IReadOnlyList<string> operationArgs,
        IReadOnlyList<string>? permittedDomains,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(componentPath))
        {
            throw new ArgumentException("Component path is required.", nameof(componentPath));
        }

        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("Operation is required.", nameof(operation));
        }

        var envelope = new WasmComponentInvokeEnvelope(operationArgs ?? [], permittedDomains);
        var argsJson = JsonSerializer.Serialize(envelope, WasmComponentInvokerJsonContext.Default.WasmComponentInvokeEnvelope);
        var timeoutMs = DefaultTimeoutMs;
        if (string.Equals(operation, "search", StringComparison.OrdinalIgnoreCase))
        {
            timeoutMs = SearchTimeoutMs;
        }

        var operationPtr = Marshal.StringToCoTaskMemUTF8(operation);
        var argsPtr = Marshal.StringToCoTaskMemUTF8(argsJson);

        try
        {
            var handle = GetOrCreatePluginHandle(componentPath);
            var result = NativeBindings.PluginInvoke(
                handle,
                operationPtr,
                argsPtr,
                timeoutMs,
                out nint outJson,
                out nint outError);

            if (result != NativeSuccess)
            {
                var nativeError = PtrToUtf8String(outError);
                if (!string.IsNullOrWhiteSpace(nativeError)
                    && nativeError.Contains("unknown plugin handle", StringComparison.OrdinalIgnoreCase)
                    && _pluginHandles.TryRemove(componentPath, out _))
                {
                    return InvokeAsync(componentPath, operation, operationArgs ?? [], permittedDomains, cancellationToken);
                }

                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(nativeError)
                        ? $"Native WASM runtime invocation failed (code {result})."
                        : nativeError);
            }

            var output = PtrToUtf8String(outJson);
            if (string.IsNullOrWhiteSpace(output))
            {
                throw new InvalidOperationException("Native WASM runtime returned empty output.");
            }

            NativeBindings.FreeString(outJson);
            NativeBindings.FreeString(outError);

            return Task.FromResult(output);
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                "Native WASM runtime library is not available. Build and bundle emma_wasm_runtime for this platform.",
                ex);
        }
        finally
        {
            Marshal.FreeCoTaskMem(operationPtr);
            Marshal.FreeCoTaskMem(argsPtr);
        }
    }

    private ulong GetOrCreatePluginHandle(string componentPath)
    {
        ThrowIfDisposed();

        var fileInfo = new FileInfo(componentPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("WASM component path was not found.", componentPath);
        }

        var currentLastWriteUtc = fileInfo.LastWriteTimeUtc;
        var currentLength = fileInfo.Length;

        if (_pluginHandles.TryGetValue(componentPath, out var existing) && existing.Handle != 0)
        {
            if (existing.LastWriteTimeUtc == currentLastWriteUtc && existing.Length == currentLength)
            {
                return existing.Handle;
            }

            NativeBindings.ClosePlugin(existing.Handle);
            _pluginHandles.TryRemove(componentPath, out _);
        }

        var handle = NativeBindings.OpenPlugin(componentPath);
        _pluginHandles[componentPath] = new CachedPluginHandle(handle, currentLastWriteUtc, currentLength);
        return handle;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var (_, cachedHandle) in _pluginHandles)
        {
            if (cachedHandle.Handle != 0)
            {
                NativeBindings.ClosePlugin(cachedHandle.Handle);
            }
        }

        _pluginHandles.Clear();
    }

    private static string? PtrToUtf8String(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.PtrToStringUTF8(ptr);
    }

    private static class NativeBindings
    {
        private const string ExternalLibraryName = "emma_wasm_runtime";
        private const string InternalLibraryName = "__Internal";

        private delegate int InvokeDelegate(
            IntPtr componentPath,
            IntPtr operation,
            IntPtr operationArgsJson,
            uint timeoutMs,
            out IntPtr outJson,
            out IntPtr outError);

        private delegate int OpenPluginDelegate(
            IntPtr componentPath,
            out ulong outHandle,
            out IntPtr outError);

        private delegate int PluginInvokeDelegate(
            ulong handle,
            IntPtr operation,
            IntPtr operationArgsJson,
            uint timeoutMs,
            out IntPtr outJson,
            out IntPtr outError);

        private delegate void PluginCloseDelegate(ulong handle);

        private delegate void FreeStringDelegate(IntPtr value);
        private delegate IntPtr TakeLastTimingDelegate();

        private static readonly Lock FallbackLock = new();
        private static InvokeDelegate? _fallbackInvoke;
        private static OpenPluginDelegate? _fallbackOpen;
        private static PluginInvokeDelegate? _fallbackPluginInvoke;
        private static PluginCloseDelegate? _fallbackPluginClose;
        private static FreeStringDelegate? _fallbackFreeString;
        private static TakeLastTimingDelegate? _fallbackTakeLastTiming;

        [DllImport(ExternalLibraryName, EntryPoint = "emma_wasm_component_invoke", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InvokeExternal(
            IntPtr componentPath,
            IntPtr operation,
            IntPtr operationArgsJson,
            uint timeoutMs,
            out IntPtr outJson,
            out IntPtr outError);

        [DllImport(InternalLibraryName, EntryPoint = "emma_wasm_component_invoke", CallingConvention = CallingConvention.Cdecl)]
        private static extern int InvokeInternal(
            IntPtr componentPath,
            IntPtr operation,
            IntPtr operationArgsJson,
            uint timeoutMs,
            out IntPtr outJson,
            out IntPtr outError);

        [DllImport(ExternalLibraryName, EntryPoint = "emma_wasm_plugin_open", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenPluginExternal(
            IntPtr componentPath,
            out ulong outHandle,
            out IntPtr outError);

        [DllImport(InternalLibraryName, EntryPoint = "emma_wasm_plugin_open", CallingConvention = CallingConvention.Cdecl)]
        private static extern int OpenPluginInternal(
            IntPtr componentPath,
            out ulong outHandle,
            out IntPtr outError);

        [DllImport(ExternalLibraryName, EntryPoint = "emma_wasm_plugin_invoke", CallingConvention = CallingConvention.Cdecl)]
        private static extern int PluginInvokeExternal(
            ulong handle,
            IntPtr operation,
            IntPtr operationArgsJson,
            uint timeoutMs,
            out IntPtr outJson,
            out IntPtr outError);

        [DllImport(InternalLibraryName, EntryPoint = "emma_wasm_plugin_invoke", CallingConvention = CallingConvention.Cdecl)]
        private static extern int PluginInvokeInternal(
            ulong handle,
            IntPtr operation,
            IntPtr operationArgsJson,
            uint timeoutMs,
            out IntPtr outJson,
            out IntPtr outError);

        [DllImport(ExternalLibraryName, EntryPoint = "emma_wasm_plugin_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PluginCloseExternal(ulong handle);

        [DllImport(InternalLibraryName, EntryPoint = "emma_wasm_plugin_close", CallingConvention = CallingConvention.Cdecl)]
        private static extern void PluginCloseInternal(ulong handle);

        [DllImport(ExternalLibraryName, EntryPoint = "emma_wasm_runtime_free_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeStringExternal(IntPtr value);

        [DllImport(InternalLibraryName, EntryPoint = "emma_wasm_runtime_free_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeStringInternal(IntPtr value);

        [DllImport(ExternalLibraryName, EntryPoint = "emma_wasm_runtime_take_last_timing", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr TakeLastTimingExternal();

        [DllImport(InternalLibraryName, EntryPoint = "emma_wasm_runtime_take_last_timing", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr TakeLastTimingInternal();

        public static int Invoke(
            IntPtr componentPath,
            IntPtr operation,
            IntPtr operationArgsJson,
            uint timeoutMs,
            out IntPtr outJson,
            out IntPtr outError)
        {
            if (UseInternalLibrary())
            {
                try
                {
                    return InvokeInternal(componentPath, operation, operationArgsJson, timeoutMs, out outJson, out outError);
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }

                if (TryResolveFallback(out var invoke, out _, out _, out _, out _, out _))
                {
                    return invoke(componentPath, operation, operationArgsJson, timeoutMs, out outJson, out outError);
                }

                throw new DllNotFoundException("WASM runtime symbols are unavailable via __Internal and fallback module resolution.");
            }

            return InvokeExternal(componentPath, operation, operationArgsJson, timeoutMs, out outJson, out outError);
        }

        public static ulong OpenPlugin(string componentPath)
        {
            var pathPtr = Marshal.StringToCoTaskMemUTF8(componentPath);
            try
            {
                ulong handle;
                IntPtr outError;
                var result = UseInternalLibrary()
                    ? TryOpenInternal(pathPtr, out handle, out outError)
                    : OpenPluginExternal(pathPtr, out handle, out outError);

                try
                {
                    if (result == NativeSuccess)
                    {
                        return handle;
                    }

                    var nativeError = Marshal.PtrToStringUTF8(outError);
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(nativeError)
                            ? $"Native WASM runtime plugin open failed (code {result})."
                            : nativeError);
                }
                finally
                {
                    FreeString(outError);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }

        private static int TryOpenInternal(IntPtr componentPath, out ulong outHandle, out IntPtr outError)
        {
            try
            {
                return OpenPluginInternal(componentPath, out outHandle, out outError);
            }
            catch (EntryPointNotFoundException)
            {
                outHandle = 0;
                outError = IntPtr.Zero;
                throw new PlatformNotSupportedException("Native runtime does not expose plugin lifecycle API.");
            }
            catch (DllNotFoundException)
            {
                if (TryResolveFallback(out _, out _, out _, out var open, out _, out _)
                    && open is not null)
                {
                    return open(componentPath, out outHandle, out outError);
                }

                outHandle = 0;
                outError = IntPtr.Zero;
                throw;
            }
        }

        public static int PluginInvoke(
            ulong handle,
            IntPtr operation,
            IntPtr operationArgsJson,
            uint timeoutMs,
            out IntPtr outJson,
            out IntPtr outError)
        {
            if (UseInternalLibrary())
            {
                try
                {
                    return PluginInvokeInternal(handle, operation, operationArgsJson, timeoutMs, out outJson, out outError);
                }
                catch (EntryPointNotFoundException)
                {
                    throw new PlatformNotSupportedException("Native runtime does not expose plugin lifecycle API.");
                }
                catch (DllNotFoundException)
                {
                    if (TryResolveFallback(out _, out _, out _, out _, out var pluginInvoke, out _)
                        && pluginInvoke is not null)
                    {
                        return pluginInvoke(handle, operation, operationArgsJson, timeoutMs, out outJson, out outError);
                    }

                    throw;
                }
            }

            return PluginInvokeExternal(handle, operation, operationArgsJson, timeoutMs, out outJson, out outError);
        }

        public static void ClosePlugin(ulong handle)
        {
            if (handle == 0)
            {
                return;
            }

            if (UseInternalLibrary())
            {
                try
                {
                    PluginCloseInternal(handle);
                    return;
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }

                if (TryResolveFallback(out _, out _, out _, out _, out _, out var close)
                    && close is not null)
                {
                    close(handle);
                }

                return;
            }

            PluginCloseExternal(handle);
        }

        public static void FreeString(IntPtr value)
        {
            if (value == IntPtr.Zero)
            {
                return;
            }

            if (UseInternalLibrary())
            {
                try
                {
                    FreeStringInternal(value);
                    return;
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }

                if (TryResolveFallback(out _, out var freeString, out _, out _, out _, out _))
                {
                    freeString(value);
                    return;
                }

                throw new DllNotFoundException("WASM runtime free-string symbol is unavailable via __Internal and fallback module resolution.");
            }

            FreeStringExternal(value);
        }

        public static IntPtr TakeLastTiming()
        {
            if (UseInternalLibrary())
            {
                try
                {
                    return TakeLastTimingInternal();
                }
                catch (DllNotFoundException)
                {
                }
                catch (EntryPointNotFoundException)
                {
                }

                if (TryResolveFallback(out _, out _, out var takeLastTiming, out _, out _, out _)
                    && takeLastTiming is not null)
                {
                    return takeLastTiming();
                }

                return IntPtr.Zero;
            }

            try
            {
                return TakeLastTimingExternal();
            }
            catch (DllNotFoundException)
            {
                return IntPtr.Zero;
            }
            catch (EntryPointNotFoundException)
            {
                return IntPtr.Zero;
            }
        }

        private static bool TryResolveFallback(
            out InvokeDelegate invoke,
            out FreeStringDelegate freeString,
            out TakeLastTimingDelegate? takeLastTiming,
            out OpenPluginDelegate? open,
            out PluginInvokeDelegate? pluginInvoke,
            out PluginCloseDelegate? pluginClose)
        {
            lock (FallbackLock)
            {
                if (_fallbackInvoke is not null && _fallbackFreeString is not null)
                {
                    invoke = _fallbackInvoke;
                    freeString = _fallbackFreeString;
                    takeLastTiming = _fallbackTakeLastTiming;
                    open = _fallbackOpen;
                    pluginInvoke = _fallbackPluginInvoke;
                    pluginClose = _fallbackPluginClose;
                    return true;
                }

                var baseDir = AppContext.BaseDirectory;
                var candidates = ResolveFallbackLibraryCandidates(baseDir);

                foreach (var candidate in candidates)
                {
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    if (!NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        continue;
                    }

                    if (!NativeLibrary.TryGetExport(handle, "emma_wasm_component_invoke", out var invokePtr))
                    {
                        continue;
                    }

                    if (!NativeLibrary.TryGetExport(handle, "emma_wasm_runtime_free_string", out var freePtr))
                    {
                        continue;
                    }

                    TakeLastTimingDelegate? fallbackTakeLastTiming = null;
                    if (NativeLibrary.TryGetExport(handle, "emma_wasm_runtime_take_last_timing", out var timingPtr))
                    {
                        fallbackTakeLastTiming = Marshal.GetDelegateForFunctionPointer<TakeLastTimingDelegate>(timingPtr);
                    }

                    _fallbackInvoke = Marshal.GetDelegateForFunctionPointer<InvokeDelegate>(invokePtr);
                    _fallbackFreeString = Marshal.GetDelegateForFunctionPointer<FreeStringDelegate>(freePtr);
                    _fallbackTakeLastTiming = fallbackTakeLastTiming;

                    if (NativeLibrary.TryGetExport(handle, "emma_wasm_plugin_open", out var openPtr))
                    {
                        _fallbackOpen = Marshal.GetDelegateForFunctionPointer<OpenPluginDelegate>(openPtr);
                    }
                    if (NativeLibrary.TryGetExport(handle, "emma_wasm_plugin_invoke", out var pluginInvokePtr))
                    {
                        _fallbackPluginInvoke = Marshal.GetDelegateForFunctionPointer<PluginInvokeDelegate>(pluginInvokePtr);
                    }
                    if (NativeLibrary.TryGetExport(handle, "emma_wasm_plugin_close", out var pluginClosePtr))
                    {
                        _fallbackPluginClose = Marshal.GetDelegateForFunctionPointer<PluginCloseDelegate>(pluginClosePtr);
                    }

                    invoke = _fallbackInvoke;
                    freeString = _fallbackFreeString;
                    takeLastTiming = _fallbackTakeLastTiming;
                    open = _fallbackOpen;
                    pluginInvoke = _fallbackPluginInvoke;
                    pluginClose = _fallbackPluginClose;
                    return true;
                }

                invoke = null!;
                freeString = null!;
                takeLastTiming = null;
                open = null;
                pluginInvoke = null;
                pluginClose = null;
                return false;
            }
        }

        private static IReadOnlyList<string> ResolveFallbackLibraryCandidates(string baseDir)
        {
            var configured = Environment.GetEnvironmentVariable("EMMA_WASM_RUNTIME_CANDIDATES");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return [.. configured
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(path => Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
            }

            return
            [
                Path.Combine(baseDir, "Runner.debug.dylib"),
                Path.Combine(baseDir, "Runner"),
                Path.Combine(baseDir, "Frameworks", "App.framework", "App")
            ];
        }

        private static bool UseInternalLibrary()
        {
            return HostPlatformPolicy.UsesInternalNativeWasmLibrary(_pluginHostOptions);
        }
    }
}
