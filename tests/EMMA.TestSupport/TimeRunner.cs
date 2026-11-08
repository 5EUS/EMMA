using Microsoft.Extensions.Time.Testing;

namespace EMMA.TestSupport;

/// <summary>Helps tests drive code that waits on a <see cref="FakeTimeProvider"/>.</summary>
public static class TimeRunner
{
    private static readonly TimeSpan FakeStep = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RealPause = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan RealLimit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Completes <paramref name="work"/> by advancing fake time in small steps, pausing briefly in
    /// real time between steps so the work's own threads can run and register their timers.
    /// Code that sleeps (retry back-off, timeouts) therefore finishes without real waiting.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The task to complete.</param>
    /// <param name="time">The fake clock that <paramref name="work"/> waits on.</param>
    /// <returns>The task's result.</returns>
    /// <exception cref="TimeoutException">The task did not finish within 10 seconds of real time.</exception>
    public static async Task<T> RunAsync<T>(Task<T> work, FakeTimeProvider time)
    {
        using CancellationTokenSource guard = new(RealLimit, TimeProvider.System);
        try
        {
            while (!work.IsCompleted)
            {
                time.Advance(FakeStep);
                await Task.WhenAny(work, Task.Delay(RealPause, guard.Token)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            throw new TimeoutException("The work did not finish within 10 seconds of real time.");
        }

        return await work.ConfigureAwait(false);
    }
}
