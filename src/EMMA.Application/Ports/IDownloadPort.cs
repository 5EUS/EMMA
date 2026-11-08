using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>Stores download jobs. A job is replaced as a whole when it changes.</summary>
public interface IDownloadPort
{
    /// <summary>Stores a new job.</summary>
    /// <param name="job">The job.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the job is stored.</returns>
    Task CreateAsync(DownloadJob job, CancellationToken cancellationToken);

    /// <summary>Finds a job.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job, or <see langword="null"/> when unknown.</returns>
    Task<DownloadJob?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Lists jobs, newest first.</summary>
    /// <param name="states">Only jobs in these states, or <see langword="null"/> for all.</param>
    /// <param name="limit">The most jobs to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The jobs.</returns>
    Task<IReadOnlyList<DownloadJob>> ListAsync(
        IReadOnlyCollection<DownloadState>? states,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>Replaces a stored job.</summary>
    /// <param name="job">The updated job.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the job existed.</returns>
    Task<bool> UpdateAsync(DownloadJob job, CancellationToken cancellationToken);

    /// <summary>Deletes a job.</summary>
    /// <param name="id">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the job existed.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
