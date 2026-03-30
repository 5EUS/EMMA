using EMMA.Domain;

namespace EMMA.Application.Ports;

public interface IDownloadPort
{
    Task<DownloadJobRecord> CreateJobAsync(DownloadEnqueueRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadJobRecord>> ListJobsAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadJobRecord>> ListJobsByStateAsync(
        IReadOnlyList<DownloadJobState> states,
        int limit,
        CancellationToken cancellationToken);

    Task<DownloadJobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken);

    Task<bool> UpdateStateAsync(
        string jobId,
        DownloadJobState state,
        string? errorMessage,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> UpdateProgressAsync(
        string jobId,
        int completed,
        int total,
        long bytesDownloaded,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);

    Task<bool> DeleteJobAsync(string jobId, CancellationToken cancellationToken);
}
