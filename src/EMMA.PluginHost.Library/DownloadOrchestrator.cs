using System.Collections.Concurrent;
using EMMA.Application.Ports;
using EMMA.Domain;
using Microsoft.Extensions.Logging;

namespace EMMA.PluginHost.Library;

public sealed class DownloadOrchestrator : IDisposable
{
    private readonly IDownloadPort _downloads;
    private readonly Func<DownloadJobRecord, IProgress<DownloadExecutionProgress>, CancellationToken, Task<DownloadExecutionResult>> _executor;
    private readonly ILogger<DownloadOrchestrator> _logger;
    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    private bool _disposed;

    public DownloadOrchestrator(
        IDownloadPort downloads,
        Func<DownloadJobRecord, IProgress<DownloadExecutionProgress>, CancellationToken, Task<DownloadExecutionResult>> executor,
        ILogger<DownloadOrchestrator> logger)
    {
        _downloads = downloads;
        _executor = executor;
        _logger = logger;
        _worker = Task.Run(ProcessLoopAsync);
    }

    public async Task<DownloadJobRecord> EnqueueAsync(DownloadEnqueueRequest request, CancellationToken cancellationToken)
    {
        var created = await _downloads.CreateJobAsync(request, cancellationToken);
        _wakeSignal.Release();
        return created;
    }

    public Task<IReadOnlyList<DownloadJobRecord>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        return _downloads.ListJobsAsync(limit, cancellationToken);
    }

    public Task<DownloadJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        return _downloads.GetJobAsync(jobId, cancellationToken);
    }

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

    public async Task<bool> DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        CancelRunningJob(jobId);
        return await _downloads.DeleteJobAsync(jobId, cancellationToken);
    }

    private async Task ProcessLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var pending = await _downloads.ListJobsByStateAsync(
                    [DownloadJobState.Queued],
                    limit: 1,
                    _shutdown.Token);

                if (pending.Count == 0)
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(5), _shutdown.Token);
                    continue;
                }

                await ProcessSingleAsync(pending[0], _shutdown.Token);
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

public sealed record DownloadExecutionProgress(int Completed, int Total, long BytesDownloaded);

public sealed record DownloadExecutionResult(
    bool Success,
    int Completed,
    int Total,
    long BytesDownloaded,
    string? ErrorMessage);
