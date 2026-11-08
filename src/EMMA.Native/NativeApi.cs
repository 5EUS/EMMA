using EMMA.Domain;
using EMMA.Runtime;

namespace EMMA.Native;

/// <summary>
/// The library without the pointers: create, call, destroy, in managed types. The exports only translate memory to and
/// from these, so behaviour is tested here and the C harness proves the marshalling.
/// </summary>
internal static class NativeApi
{
    /// <summary>The version of the C ABI. Bumped only for a change that breaks existing callers.</summary>
    public const int AbiVersion = 2;

    /// <summary>Creates a runtime and opens a handle to it. Blocks until the runtime is ready.</summary>
    /// <param name="config">The UTF-8 JSON configuration.</param>
    /// <param name="handle">The handle when creation succeeds.</param>
    /// <param name="error">The error envelope when it does not.</param>
    /// <returns>A <see cref="NativeStatus"/>.</returns>
    public static int Create(ReadOnlySpan<byte> config, out nint handle, out NativeResponse? error)
    {
        handle = 0;
        error = null;
        Outcome<RuntimeConfig> parsed = RuntimeConfigReader.Parse(config);
        if (!parsed.IsSuccess)
        {
            error = Envelope.Failure(parsed.Error);
            return NativeStatus.InvalidArgument;
        }

        // The session owns the runtime and, once it is in the table, so does the table until Close; the disposal analyzer cannot follow that.
#pragma warning disable CA2000
        NativeSession session = new(new EmmaRuntime(parsed.Value.ToOptions()), parsed.Value.User);
#pragma warning restore CA2000
        try
        {
            Blocking.Run(async () =>
            {
                await session.InitializeAsync().ConfigureAwait(false);
                return true;
            });
        }
        catch (Exception ex)
        {
            NativeDiagnostics.Report(ex);
            Blocking.Run(async () =>
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return true;
            });
            error = Envelope.Fault("runtime_error", $"EMMA could not start ({ex.GetType().Name}): {ex.Message}");
            return NativeStatus.Runtime;
        }

        handle = SessionTable.Add(session);
        return NativeStatus.Ok;
    }

    /// <summary>Runs an operation and waits for the result.</summary>
    /// <param name="handle">The handle from <see cref="Create"/>.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="request">The UTF-8 JSON request.</param>
    /// <returns>A <see cref="NativeStatus"/> and the response.</returns>
    public static (int Status, NativeResponse Response) Invoke(nint handle, string operation, byte[] request) =>
        Blocking.Run(() => CallAsync(handle, operation, request));

    /// <summary>Runs an operation on the thread pool and reports the result to a delegate.</summary>
    /// <param name="handle">The handle from <see cref="Create"/>.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="request">The UTF-8 JSON request.</param>
    /// <param name="deliver">Receives the status and response, once.</param>
    public static void CallInBackground(nint handle, string operation, byte[] request, Action<int, NativeResponse> deliver) =>
        _ = Task.Run(async () =>
        {
            (int status, NativeResponse response) = await CallAsync(handle, operation, request).ConfigureAwait(false);
            deliver(status, response);
        });

    /// <summary>Runs an operation.</summary>
    /// <param name="handle">The handle from <see cref="Create"/>.</param>
    /// <param name="operation">The operation name.</param>
    /// <param name="request">The UTF-8 JSON request.</param>
    /// <returns>A <see cref="NativeStatus"/> and the response.</returns>
    public static async Task<(int Status, NativeResponse Response)> CallAsync(nint handle, string operation, byte[] request)
    {
        NativeSession? session = SessionTable.Find(handle);
        if (session is null)
        {
            return (NativeStatus.InvalidArgument, Envelope.Fault("invalid_handle", "The handle is not open."));
        }

        return (NativeStatus.Ok, await session.InvokeAsync(operation, request).ConfigureAwait(false));
    }

    /// <summary>Closes a handle, blocking until calls in flight have been cancelled and finished. Closing an unknown or closed handle does nothing.</summary>
    /// <param name="handle">The handle.</param>
    public static void Close(nint handle)
    {
        try
        {
            Blocking.Run(async () =>
            {
                await DestroyAsync(handle).ConfigureAwait(false);
                return true;
            });
        }
        catch (Exception ex)
        {
            // Nothing can be returned from a void export, and the handle is already forgotten.
            NativeDiagnostics.Report(ex);
        }
    }

    /// <summary>Closes a handle.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>A task that completes when the runtime is disposed.</returns>
    public static async Task DestroyAsync(nint handle)
    {
        NativeSession? session = SessionTable.Remove(handle);
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
