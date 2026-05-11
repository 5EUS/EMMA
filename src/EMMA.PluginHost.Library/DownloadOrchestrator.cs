using System.Collections.Concurrent;
using EMMA.Application.Ports;
using EMMA.Domain;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginHost.Library;

/// <summary>
/// Coordinates queued download jobs and executes them in the background.
/// </summary>
public sealed class DownloadOrchestrator : IDisposable
{
    private readonly IDownloadPort _downloads;
    private readonly Func<DownloadJobRecord, IProgress<DownloadExecutionProgress>, CancellationToken, Task<DownloadExecutionResult>> _executor;
    private readonly DownloadExecutionOptions _options;
    private readonly ILogger<DownloadOrchestrator> _logger;
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    private bool _disposed;
    private int _activeJobCount;

    /// <summary>
    /// Creates a download orchestrator that queues, executes, and tracks download jobs.
    /// </summary>
    /// <param name="downloads">The download persistence port used to store job state.</param>
    /// <param name="executor">Executes a single download job and reports progress updates.</param>
    /// <param name="options">Controls concurrent background download execution.</param>
    /// <param name="logger">The logger used for background processing diagnostics.</param>
    public DownloadOrchestrator(
        IDownloadPort downloads,
        Func<DownloadJobRecord, IProgress<DownloadExecutionProgress>, CancellationToken, Task<DownloadExecutionResult>> executor,
        DownloadExecutionOptions options,
        ILogger<DownloadOrchestrator> logger)
    {
        _downloads = downloads;
        _executor = executor;
        _options = options;
        _logger = logger;
        _worker = Task.Run(ProcessLoopAsync);
    }

    /// <summary>
    /// Creates and queues a new download job for background execution.
    /// </summary>
    /// <param name="request">The download request to enqueue.</param>
    /// <param name="cancellationToken">Cancels the enqueue operation.</param>
    /// <returns>The persisted download job record.</returns>
    public async Task<DownloadJobRecord> EnqueueAsync(DownloadEnqueueRequest request, CancellationToken cancellationToken)
    {
        var created = await _downloads.CreateJobAsync(request, cancellationToken);
        _wakeSignal.Release();
        return created;
    }

    /// <summary>
    /// Lists recent download jobs.
    /// </summary>
    /// <param name="limit">The maximum number of jobs to return.</param>
    /// <param name="cancellationToken">Cancels the list operation.</param>
    /// <returns>The requested download job records.</returns>
    public Task<IReadOnlyList<DownloadJobRecord>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        return _downloads.ListJobsAsync(limit, cancellationToken);
    }

    /// <summary>
    /// Retrieves a single download job by identifier.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <param name="cancellationToken">Cancels the lookup operation.</param>
    /// <returns>The matching download job, or <see langword="null"/> when no job exists.</returns>
    public Task<DownloadJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        return _downloads.GetJobAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Pauses a queued or running download job.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <param name="cancellationToken">Cancels the state update.</param>
    /// <returns><see langword="true"/> when the job state was updated; otherwise <see langword="false"/>.</returns>
    public async Task<bool> PauseAsync(string jobId, CancellationToken cancellationToken)
    {
        CancelRunningJob(jobId);
        return await _downloads.UpdateStateAsync(
            jobId,
            DownloadJobState.Paused,
            null,
            DateTimeOffset.UtcNow,
            startedAtUtc: null,
            completedAtUtc: null,
            cancellationToken);
    }

            /// <summary>
            /// Re-queues a paused download job.
            /// </summary>
            /// <param name="jobId">The download job identifier.</param>
            /// <param name="cancellationToken">Cancels the state update.</param>
            /// <returns><see langword="true"/> when the job state was updated; otherwise <see langword="false"/>.</returns>
    public async Task<bool> ResumeAsync(string jobId, CancellationToken cancellationToken)
    {
        var updated = await _downloads.UpdateStateAsync(
            jobId,
            DownloadJobState.Queued,
            null,
            DateTimeOffset.UtcNow,
            startedAtUtc: null,
            completedAtUtc: null,
            cancellationToken);

        if (updated)
        {
            _wakeSignal.Release();
        }

        return updated;
    }

    /// <summary>
    /// Re-queues a failed download job and clears stale progress and error state.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <param name="cancellationToken">Cancels the retry operation.</param>
    /// <returns><see langword="true"/> when the job was updated; otherwise <see langword="false"/>.</returns>
    public async Task<bool> RetryAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await _downloads.GetJobAsync(jobId, cancellationToken);
        if (job is null || job.State != DownloadJobState.Failed)
        {
            return false;
        }

        await _downloads.UpdateProgressAsync(
            jobId,
            completed: 0,
            total: 0,
            bytesDownloaded: 0,
            updatedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken);

        var updated = await _downloads.UpdateStateAsync(
            jobId,
            DownloadJobState.Queued,
            null,
            DateTimeOffset.UtcNow,
            startedAtUtc: null,
            completedAtUtc: null,
            cancellationToken);

        if (updated)
        {
            _wakeSignal.Release();
        }

        return updated;
    }

    /// <summary>
    /// Cancels a queued or running download job.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <param name="cancellationToken">Cancels the state update.</param>
    /// <returns><see langword="true"/> when the job state was updated; otherwise <see langword="false"/>.</returns>
    public async Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken)
    {
        CancelRunningJob(jobId);

        return await _downloads.UpdateStateAsync(
            jobId,
            DownloadJobState.Canceled,
            null,
            DateTimeOffset.UtcNow,
            startedAtUtc: null,
            completedAtUtc: DateTimeOffset.UtcNow,
            cancellationToken);
    }

    /// <summary>
    /// Deletes a download job and stops any active execution for it.
    /// </summary>
    /// <param name="jobId">The download job identifier.</param>
    /// <param name="cancellationToken">Cancels the delete operation.</param>
    /// <returns><see langword="true"/> when the job was deleted; otherwise <see langword="false"/>.</returns>
    public async Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        CancelRunningJob(jobId);
        return await _downloads.DeleteJobAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Wakes the background worker so it can react to an updated download capacity limit.
    /// </summary>
    public void NotifyCapacityChanged()
    {
        if (_disposed)
        {
            return;
        }

        TryReleaseWakeSignal();
    }

    private async Task ProcessLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var availableSlots = Math.Max(
                    0,
                    _options.MaxConcurrentDownloads - Volatile.Read(ref _activeJobCount));

                if (availableSlots == 0)
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(5), _shutdown.Token);
                    continue;
                }

                var pending = await _downloads.ListJobsByStateAsync(
                    [DownloadJobState.Queued],
                    limit: availableSlots,
                    _shutdown.Token);

                if (pending.Count == 0)
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(5), _shutdown.Token);
                    continue;
                }

                foreach (var queuedJob in pending)
                {
                    Interlocked.Increment(ref _activeJobCount);
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await ProcessSingleAsync(queuedJob, _shutdown.Token);
                            }
                            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                            {
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Download job '{JobId}' faulted.", queuedJob.Id);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref _activeJobCount);
                                if (!_shutdown.IsCancellationRequested)
                                {
                                    TryReleaseWakeSignal();
                                }
                            }
                        },
                        CancellationToken.None);
                }

                if (Volatile.Read(ref _activeJobCount) < _options.MaxConcurrentDownloads)
                {
                    TryReleaseWakeSignal();
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download orchestrator loop faulted.");
                await Task.Delay(TimeSpan.FromSeconds(1), _shutdown.Token);
            }
        }
    }

    private async Task ProcessSingleAsync(DownloadJobRecord queuedJob, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var movedToRunning = await _downloads.UpdateStateAsync(
            queuedJob.Id,
            DownloadJobState.Running,
            null,
            now,
            startedAtUtc: now,
            completedAtUtc: null,
            cancellationToken);

        if (!movedToRunning)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_runningJobs.TryAdd(queuedJob.Id, cts))
        {
            cts.Dispose();
            return;
        }

        try
        {
            var progress = new SynchronousProgress<DownloadExecutionProgress>(update =>
            {
                _downloads.UpdateProgressAsync(
                        queuedJob.Id,
                        update.Completed,
                        update.Total,
                        update.BytesDownloaded,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            });

            var result = await _executor(queuedJob, progress, cts.Token);
            var finishedAt = DateTimeOffset.UtcNow;

            await _downloads.UpdateProgressAsync(
                queuedJob.Id,
                result.Completed,
                result.Total,
                result.BytesDownloaded,
                finishedAt,
                cancellationToken);

            await _downloads.UpdateStateAsync(
                queuedJob.Id,
                result.Success ? DownloadJobState.Completed : DownloadJobState.Failed,
                result.ErrorMessage,
                finishedAt,
                startedAtUtc: null,
                completedAtUtc: finishedAt,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var latest = await _downloads.GetJobAsync(queuedJob.Id, CancellationToken.None);
            if (latest is null)
            {
                return;
            }

            if (latest.State is DownloadJobState.Paused or DownloadJobState.Canceled)
            {
                return;
            }

            await _downloads.UpdateStateAsync(
                queuedJob.Id,
                DownloadJobState.Canceled,
                null,
                DateTimeOffset.UtcNow,
                startedAtUtc: null,
                completedAtUtc: DateTimeOffset.UtcNow,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _downloads.UpdateStateAsync(
                queuedJob.Id,
                DownloadJobState.Failed,
                ex.Message,
                DateTimeOffset.UtcNow,
                startedAtUtc: null,
                completedAtUtc: DateTimeOffset.UtcNow,
                CancellationToken.None);
        }
        finally
        {
            if (_runningJobs.TryRemove(queuedJob.Id, out var runningCts))
            {
                runningCts.Dispose();
            }
        }
    }

    private void CancelRunningJob(string jobId)
    {
        if (_runningJobs.TryGetValue(jobId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }
        }
    }

    private void TryReleaseWakeSignal()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value)
        {
            _handler(value);
        }
    }

    /// <summary>
    /// Stops background processing and releases orchestrator resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown.Cancel();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch
        {
        }

        foreach (var pair in _runningJobs)
        {
            pair.Value.Cancel();
            pair.Value.Dispose();
        }

        _runningJobs.Clear();
        _wakeSignal.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>
/// Represents incremental progress reported by a download execution.
/// </summary>
/// <param name="Completed">The completed work-unit count.</param>
/// <param name="Total">The total work-unit count.</param>
/// <param name="BytesDownloaded">The total downloaded bytes.</param>
public sealed record DownloadExecutionProgress(int Completed, int Total, long BytesDownloaded);

/// <summary>
/// Represents the final outcome of a download execution.
/// </summary>
/// <param name="Success">Indicates whether the execution succeeded.</param>
/// <param name="Completed">The completed work-unit count.</param>
/// <param name="Total">The total work-unit count.</param>
/// <param name="BytesDownloaded">The total downloaded bytes.</param>
/// <param name="ErrorMessage">The optional failure message.</param>
public sealed record DownloadExecutionResult(
    bool Success,
    int Completed,
    int Total,
    long BytesDownloaded,
    string? ErrorMessage);
