using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace EMMA.Native;

/// <summary>
/// The C ABI, exactly as declared in <c>include/emma_native.h</c>. These methods only move bytes between the
/// caller's memory and <see cref="NativeApi"/>; none of them throws into the caller.
/// </summary>
internal static class NativeExports
{
    /// <summary>Gets the version of the ABI.</summary>
    /// <returns>The version.</returns>
    [UnmanagedCallersOnly(EntryPoint = "emma_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetAbiVersion() => NativeApi.AbiVersion;

    /// <summary>Creates a runtime.</summary>
    /// <param name="config">The UTF-8 JSON configuration.</param>
    /// <param name="configLength">Its length.</param>
    /// <param name="runtime">Receives the handle.</param>
    /// <param name="error">Receives an error envelope on failure.</param>
    /// <returns>A <see cref="NativeStatus"/>.</returns>
    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_create", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int RuntimeCreate(byte* config, nuint configLength, nint* runtime, NativeBuffer* error)
    {
        if (runtime is null || (config is null && configLength != 0) || configLength > int.MaxValue)
        {
            return NativeStatus.InvalidArgument;
        }

        *runtime = 0;
        if (error is not null)
        {
            *error = default;
        }

        try
        {
            int status = NativeApi.Create(new ReadOnlySpan<byte>(config, (int)configLength), out nint handle, out NativeResponse? failure);
            *runtime = handle;
            if (failure is not null && error is not null)
            {
                *error = NativeBuffer.Copy(failure.Json);
            }

            return status;
        }
        catch (Exception ex)
        {
            NativeDiagnostics.Report(ex);
            return NativeStatus.Panic;
        }
    }

    /// <summary>Closes a handle.</summary>
    /// <param name="runtime">The handle.</param>
    [UnmanagedCallersOnly(EntryPoint = "emma_runtime_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static void RuntimeDestroy(nint runtime) => NativeApi.Close(runtime);

    /// <summary>Runs an operation and waits for the result.</summary>
    /// <param name="runtime">The handle.</param>
    /// <param name="operation">The operation name, UTF-8.</param>
    /// <param name="operationLength">Its length.</param>
    /// <param name="request">The UTF-8 JSON request.</param>
    /// <param name="requestLength">Its length; zero means <c>{}</c>.</param>
    /// <param name="json">Receives the JSON envelope.</param>
    /// <param name="blob">Receives binary content, or an empty buffer.</param>
    /// <returns>A <see cref="NativeStatus"/>.</returns>
    [UnmanagedCallersOnly(EntryPoint = "emma_call", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int Call(nint runtime, byte* operation, nuint operationLength, byte* request, nuint requestLength, NativeBuffer* json, NativeBuffer* blob)
    {
        if (json is null || blob is null)
        {
            return NativeStatus.InvalidArgument;
        }

        *json = default;
        *blob = default;
        try
        {
            if (!TryReadCall(operation, operationLength, request, requestLength, out string name, out byte[] body))
            {
                *json = NativeBuffer.Copy(Envelope.Fault("invalid_argument", "The operation or request is missing.").Json);
                return NativeStatus.InvalidArgument;
            }

            (int status, NativeResponse response) = NativeApi.Invoke(runtime, name, body);
            Fill(json, blob, response);
            return status;
        }
        catch (Exception ex)
        {
            NativeDiagnostics.Report(ex);
            return NativeStatus.Panic;
        }
    }

    /// <summary>Starts an operation and returns at once; the callback receives the result on another thread.</summary>
    /// <param name="runtime">The handle.</param>
    /// <param name="operation">The operation name, UTF-8.</param>
    /// <param name="operationLength">Its length.</param>
    /// <param name="request">The UTF-8 JSON request. It is copied; the caller may free it on return.</param>
    /// <param name="requestLength">Its length; zero means <c>{}</c>.</param>
    /// <param name="callback">Called once with the user data, the status, and the JSON and binary content as pointer and length pairs the callback owns and must free.</param>
    /// <param name="userData">Passed back to the callback untouched.</param>
    /// <returns>Zero when the call was started; otherwise the callback is never called.</returns>
    [UnmanagedCallersOnly(EntryPoint = "emma_call_async", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int CallAsync(
        nint runtime,
        byte* operation,
        nuint operationLength,
        byte* request,
        nuint requestLength,
        delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, byte*, nuint, void> callback,
        nint userData)
    {
        if (callback is null)
        {
            return NativeStatus.InvalidArgument;
        }

        try
        {
            if (!TryReadCall(operation, operationLength, request, requestLength, out string name, out byte[] body))
            {
                return NativeStatus.InvalidArgument;
            }

            nint function = (nint)callback;
            NativeApi.CallInBackground(runtime, name, body, (status, response) => Deliver(function, userData, status, response));
            return NativeStatus.Ok;
        }
        catch (Exception ex)
        {
            NativeDiagnostics.Report(ex);
            return NativeStatus.Panic;
        }
    }

    /// <summary>Releases memory the library returned.</summary>
    /// <param name="data">The <c>data</c> of a buffer, or null.</param>
    [UnmanagedCallersOnly(EntryPoint = "emma_buffer_free", CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe void BufferFree(byte* data) => NativeMemory.Free(data);

    private static unsafe void Deliver(nint function, nint userData, int status, NativeResponse response)
    {
        NativeBuffer json = NativeBuffer.Copy(response.Json);
        NativeBuffer blob = response.Blob is null ? default : NativeBuffer.Copy(response.Blob);

        // By value, pointing at heap memory the callback now owns: nothing refers to this stack frame once the callback returns.
        ((delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, byte*, nuint, void>)function)(userData, status, json.Data, json.Length, blob.Data, blob.Length);
    }

    private static unsafe void Fill(NativeBuffer* json, NativeBuffer* blob, NativeResponse response)
    {
        *json = NativeBuffer.Copy(response.Json);
        *blob = response.Blob is null ? default : NativeBuffer.Copy(response.Blob);
    }

    private static unsafe bool TryReadCall(byte* operation, nuint operationLength, byte* request, nuint requestLength, out string name, out byte[] body)
    {
        name = string.Empty;
        body = [];
        if (operation is null || operationLength == 0 || operationLength > 128 || (request is null && requestLength != 0) || requestLength > int.MaxValue)
        {
            return false;
        }

        name = Encoding.UTF8.GetString(operation, (int)operationLength);
        body = requestLength == 0 ? [] : new ReadOnlySpan<byte>(request, (int)requestLength).ToArray();
        return true;
    }
}
