using EMMA.Domain;

namespace EMMA.Application.Ports;

/// <summary>Stores download jobs and the leaves each one downloads. A job is replaced as a whole when it changes.</summary>
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

    /// <summary>Finds the most recent job for a node and role.</summary>
    /// <param name="node">The node.</param>
    /// <param name="role">The asset role.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The job, or <see langword="null"/> when there is none.</returns>
    Task<DownloadJob?> FindAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken);

    /// <summary>Replaces a stored job, but only if it is still in the state the caller last saw: the executor and a user's pause race, and the user wins.</summary>
    /// <param name="job">The updated job.</param>
    /// <param name="expected">The state the stored job must be in.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the job existed in that state and was replaced.</returns>
    Task<bool> TryTransitionAsync(DownloadJob job, DownloadState expected, CancellationToken cancellationToken);

    /// <summary>Stores the leaves a job will download.</summary>
    /// <param name="items">The leaves; one job's, in order.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when they are stored, all or none.</returns>
    Task AddItemsAsync(IReadOnlyList<DownloadItem> items, CancellationToken cancellationToken);

    /// <summary>Lists a job's leaves in order.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The leaves; empty before the job has walked its node.</returns>
    Task<IReadOnlyList<DownloadItem>> ListItemsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Replaces what is stored for a leaf: its file, type, size and last error.</summary>
    /// <param name="item">The updated leaf, found by job and position.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="true"/> when the leaf existed.</returns>
    Task<bool> UpdateItemAsync(DownloadItem item, CancellationToken cancellationToken);

    /// <summary>Finds a leaf whose asset is stored, whichever job stored it.</summary>
    /// <param name="node">The leaf node.</param>
    /// <param name="role">The asset role.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The leaf, or <see langword="null"/> when nothing is stored for it.</returns>
    Task<DownloadItem?> FindStoredAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken);

    /// <summary>Lists the stored files that only this job's leaves name, so that deleting the job leaves them unreferenced.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The file names.</returns>
    Task<IReadOnlyList<string>> ListExclusiveFilesAsync(Guid jobId, CancellationToken cancellationToken);
}
