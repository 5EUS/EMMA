using System.Runtime.InteropServices;
using System.Text.Json;

namespace EMMA.PluginHost.Services;

public sealed class NativeInProcessWasmComponentInvoker : IWasmComponentInvoker
{
    private const int NativeSuccess = 0;

    public Task<string> InvokeAsync(
        string componentPath,
        string operation,
        IReadOnlyList<string> operationArgs,
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

        var argsJson = JsonSerializer.Serialize(operationArgs ?? [], WasmComponentInvokerJsonContext.Default.IReadOnlyListString);
        var timeoutMs = 30_000u;

        var componentPtr = Marshal.StringToCoTaskMemUTF8(componentPath);
        var operationPtr = Marshal.StringToCoTaskMemUTF8(operation);
        var argsPtr = Marshal.StringToCoTaskMemUTF8(argsJson);

        try
        {
            IntPtr outJson;
            IntPtr outError;
            var result = NativeBindings.Invoke(
                componentPtr,
                operationPtr,
                argsPtr,
                timeoutMs,
                out outJson,
                out outError);

            try
            {
                if (result != NativeSuccess)
                {
                    var nativeError = PtrToUtf8String(outError);
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

                return Task.FromResult(output);
            }
            finally
            {
                NativeBindings.FreeString(outJson);
                NativeBindings.FreeString(outError);
            }
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                "Native WASM runtime library is not available. Build and bundle emma_wasm_runtime for this platform.",
                ex);
        }
        finally
        {
            Marshal.FreeCoTaskMem(componentPtr);
            Marshal.FreeCoTaskMem(operationPtr);
            Marshal.FreeCoTaskMem(argsPtr);
        }
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

        [DllImport(ExternalLibraryName, EntryPoint = "emma_wasm_runtime_free_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeStringExternal(IntPtr value);

        [DllImport(InternalLibraryName, EntryPoint = "emma_wasm_runtime_free_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FreeStringInternal(IntPtr value);

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
                return InvokeInternal(componentPath, operation, operationArgsJson, timeoutMs, out outJson, out outError);
            }

            return InvokeExternal(componentPath, operation, operationArgsJson, timeoutMs, out outJson, out outError);
        }

        public static void FreeString(IntPtr value)
        {
            if (value == IntPtr.Zero)
            {
                return;
            }

            if (UseInternalLibrary())
            {
                FreeStringInternal(value);
                return;
            }

            FreeStringExternal(value);
        }

        private static bool UseInternalLibrary()
        {
            return OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst();
        }
    }
}
