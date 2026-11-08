using System.Runtime.ExceptionServices;

namespace EMMA.Native;

/// <summary>
/// Runs asynchronous work to completion for the blocking calls. The caller of the C ABI is a foreign thread,
/// not a thread-pool thread, so blocking it cannot starve the pool or deadlock a synchronization context; the work
/// runs on the pool and the caller waits on an event. Asynchronous callers use <c>emma_call_async</c> instead.
/// </summary>
internal static class Blocking
{
    /// <summary>Runs work and waits for its result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work to run.</param>
    /// <returns>The result. A failure is rethrown with its original stack.</returns>
    public static T Run<T>(Func<Task<T>> work)
    {
        T? result = default;
        Exception? failure = null;
        using ManualResetEventSlim done = new(initialState: false);
        _ = Task.Run(async () =>
        {
            try
            {
                result = await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }
}
