using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>
/// Runs a plugin call with a per-attempt timeout and retries transient failures
/// (rate limited, unavailable). Permanent failures return at once. Time comes from
/// <see cref="TimeProvider"/> so tests control it.
/// </summary>
/// <param name="options">Retry and timeout behaviour.</param>
/// <param name="time">The time source.</param>
public sealed class PluginCallExecutor(PluginCallOptions options, TimeProvider time)
{
    /// <summary>Runs <paramref name="call"/>, retrying transient failures.</summary>
    /// <typeparam name="T">The success value type.</typeparam>
    /// <param name="call">The call; receives a token that also fires on timeout.</param>
    /// <param name="cancellationToken">The caller's token. Cancelling it throws; it is never retried.</param>
    /// <returns>The first success, the first permanent failure, or the last transient failure.</returns>
    public async Task<Outcome<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Outcome<T>>> call,
        CancellationToken cancellationToken)
    {
        int maxAttempts = Math.Max(1, options.MaxAttempts);
        int attempt = 0;
        while (true)
        {
            attempt++;
            Outcome<T> outcome = await this.AttemptAsync(call, cancellationToken).ConfigureAwait(false);
            if (outcome.IsSuccess || !outcome.Error.IsTransient || attempt >= maxAttempts)
            {
                return outcome;
            }

            TimeSpan delay = outcome.Error.RetryAfter ?? options.BaseDelay * (1 << (attempt - 1));
            if (delay > options.MaxDelay)
            {
                delay = options.MaxDelay;
            }

            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Outcome<T>> AttemptAsync<T>(
        Func<CancellationToken, Task<Outcome<T>>> call,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(options.AttemptTimeout, time);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await call(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Outcome.Failure<T>(ErrorCode.Unavailable, "The plugin did not answer in time.");
        }
    }
}
