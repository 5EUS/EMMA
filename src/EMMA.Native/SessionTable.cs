using System.Collections.Concurrent;

namespace EMMA.Native;

/// <summary>
/// The open handles. A handle is a number, never a pointer, and numbers are not reused, so a caller that uses a handle
/// after closing it (or invents one) gets an error instead of undefined behaviour. This is process-wide state
/// because the C ABI is process-wide: the one place the library cannot avoid it.
/// </summary>
internal static class SessionTable
{
    private static readonly ConcurrentDictionary<nint, NativeSession> Sessions = new();
    private static long lastHandle;

    /// <summary>Gets the number of open handles.</summary>
    public static int Count => Sessions.Count;

    /// <summary>Registers a session.</summary>
    /// <param name="session">The session.</param>
    /// <returns>Its handle.</returns>
    public static nint Add(NativeSession session)
    {
        nint handle = (nint)Interlocked.Increment(ref lastHandle);
        Sessions[handle] = session;
        return handle;
    }

    /// <summary>Finds an open session.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>The session, or <see langword="null"/> when the handle is unknown or closed.</returns>
    public static NativeSession? Find(nint handle) => Sessions.GetValueOrDefault(handle);

    /// <summary>Forgets a session.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>The session, or <see langword="null"/> when the handle was unknown or already closed.</returns>
    public static NativeSession? Remove(nint handle) => Sessions.TryRemove(handle, out NativeSession? session) ? session : null;
}
