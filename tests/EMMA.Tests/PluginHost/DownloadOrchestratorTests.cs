using System.Collections.Concurrent;
using EMMA.Application.Ports;
using EMMA.Domain;
using EMMA.PluginHost.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace EMMA.Tests.PluginHost;

public sealed class DownloadOrchestratorTests
{
    [Fact]
    public async Task ProcessesNoMoreThanConfiguredConcurrentJobs()
    {
        var port = new InMemoryDownloadPort(CreateQueuedJobs(3));
        var options = new DownloadExecutionOptions { MaxConcurrentDownloads = 2 };
        var releaseAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTwoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedJobIds = new ConcurrentQueue<string>();
        var completedCount = 0;
        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;

        using var orchestrator = new DownloadOrchestrator(
            port,
            async (job, _, cancellationToken) =>
            {
                startedJobIds.Enqueue(job.Id);
                var current = Interlocked.Increment(ref currentConcurrency);
                UpdateMax(ref maxObservedConcurrency, current);
                if (startedJobIds.Count == 2)
                {
                    firstTwoStarted.TrySetResult();
                }

                await releaseAll.Task.WaitAsync(cancellationToken);

                Interlocked.Decrement(ref currentConcurrency);
                if (Interlocked.Increment(ref completedCount) == 3)
                {
                    allCompleted.TrySetResult();
                }

                return new DownloadExecutionResult(true, 1, 1, 128, null);
            },
            options,
            NullLogger<DownloadOrchestrator>.Instance);

        orchestrator.NotifyCapacityChanged();

        await firstTwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, startedJobIds.Count);

        releaseAll.TrySetResult();
        await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, maxObservedConcurrency);
        Assert.All(port.Snapshot(), job => Assert.Equal(DownloadJobState.Completed, job.State));
    }

    [Fact]
    public async Task IncreasingCapacityStartsAdditionalQueuedJobWithoutWaitingForCompletion()
    {
        var port = new InMemoryDownloadPort(CreateQueuedJobs(2));
        var options = new DownloadExecutionOptions { MaxConcurrentDownloads = 1 };
        var releaseAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();

        using var orchestrator = new DownloadOrchestrator(
            port,
            async (job, _, cancellationToken) =>
            {
                order.Enqueue(job.Id);
                if (order.Count == 1)
                {
                    firstStarted.TrySetResult();
                }
                else if (order.Count == 2)
                {
                    secondStarted.TrySetResult();
                }

                await releaseAll.Task.WaitAsync(cancellationToken);
                return new DownloadExecutionResult(true, 1, 1, 64, null);
            },
            options,
            NullLogger<DownloadOrchestrator>.Instance);

        orchestrator.NotifyCapacityChanged();

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(secondStarted.Task.IsCompleted);

        options.MaxConcurrentDownloads = 2;
        orchestrator.NotifyCapacityChanged();

        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseAll.TrySetResult();

        await AssertEventuallyAsync(
            () => port.Snapshot().All(job => job.State == DownloadJobState.Completed),
            TimeSpan.FromSeconds(5));
    }

    private static IReadOnlyList<DownloadJobRecord> CreateQueuedJobs(int count)
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(1, count)
            .Select(index => new DownloadJobRecord(
                Id: $"job-{index}",
                PluginId: "plugin.test",
                MediaId: $"media-{index}",
                MediaType: "paged",
                ChapterId: $"chapter-{index}",
                StreamId: null,
                State: DownloadJobState.Queued,
                ProgressCompleted: 0,
                ProgressTotal: 0,
                BytesDownloaded: 0,
                ErrorMessage: null,
                CreatedAtUtc: now,
                UpdatedAtUtc: now,
                StartedAtUtc: null,
                CompletedAtUtc: null))
            .ToArray();
    }

    private static async Task AssertEventuallyAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Yield();
        }

        Assert.True(predicate());
    }

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= value)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private sealed class InMemoryDownloadPort : IDownloadPort
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, DownloadJobRecord> _jobs;

        public InMemoryDownloadPort(IReadOnlyList<DownloadJobRecord> initialJobs)
        {
            _jobs = initialJobs.ToDictionary(job => job.Id, StringComparer.Ordinal);
        }

        public Task<DownloadJobRecord> CreateJobAsync(DownloadEnqueueRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var record = new DownloadJobRecord(
                Guid.NewGuid().ToString("n"),
                request.PluginId,
                request.MediaId,
                request.MediaType,
                request.ChapterId,
                request.StreamId,
                DownloadJobState.Queued,
                0,
                0,
                0,
                null,
                now,
                now,
                null,
                null);

            lock (_gate)
            {
                _jobs[record.Id] = record;
            }

            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<DownloadJobRecord>> ListJobsAsync(int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<DownloadJobRecord>>(
                    _jobs.Values
                        .OrderBy(job => job.CreatedAtUtc)
                        .Take(limit)
                        .ToArray());
            }
        }

        public Task<IReadOnlyList<DownloadJobRecord>> ListJobsByStateAsync(
            IReadOnlyList<DownloadJobState> states,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<DownloadJobRecord>>(
                    _jobs.Values
                        .Where(job => states.Contains(job.State))
                        .OrderBy(job => job.CreatedAtUtc)
                        .Take(limit)
                        .ToArray());
            }
        }

        public Task<DownloadJobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _jobs.TryGetValue(jobId, out var job);
                return Task.FromResult(job);
            }
        }

        public Task<bool> UpdateStateAsync(
            string jobId,
            DownloadJobState state,
            string? errorMessage,
            DateTimeOffset updatedAtUtc,
            DateTimeOffset? startedAtUtc,
            DateTimeOffset? completedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_jobs.TryGetValue(jobId, out var current))
                {
                    return Task.FromResult(false);
                }

                if (state == DownloadJobState.Running && current.State != DownloadJobState.Queued)
                {
                    return Task.FromResult(false);
                }

                _jobs[jobId] = current with
                {
                    State = state,
                    ErrorMessage = errorMessage,
                    UpdatedAtUtc = updatedAtUtc,
                    StartedAtUtc = startedAtUtc ?? current.StartedAtUtc,
                    CompletedAtUtc = completedAtUtc
                };
                return Task.FromResult(true);
            }
        }

        public Task<bool> UpdateProgressAsync(
            string jobId,
            int completed,
            int total,
            long bytesDownloaded,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_jobs.TryGetValue(jobId, out var current))
                {
                    return Task.FromResult(false);
                }

                _jobs[jobId] = current with
                {
                    ProgressCompleted = completed,
                    ProgressTotal = total,
                    BytesDownloaded = bytesDownloaded,
                    UpdatedAtUtc = updatedAtUtc
                };
                return Task.FromResult(true);
            }
        }

        public Task<bool> DeleteJobAsync(string jobId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_jobs.Remove(jobId));
            }
        }

        public IReadOnlyList<DownloadJobRecord> Snapshot()
        {
            lock (_gate)
            {
                return _jobs.Values.OrderBy(job => job.CreatedAtUtc).ToArray();
            }
        }
    }
}