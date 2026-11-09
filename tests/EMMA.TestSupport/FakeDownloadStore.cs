using EMMA.Application.Ports;
using EMMA.Domain;

namespace EMMA.TestSupport;

/// <summary>An in-memory <see cref="IDownloadPort"/> that behaves like the SQLite one: jobs, their leaves, and deleting a job deletes its leaves.</summary>
public sealed class FakeDownloadStore : IDownloadPort
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, DownloadJob> jobs = [];
    private readonly List<DownloadItem> stored = [];

    /// <inheritdoc/>
    public Task CreateAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            this.jobs.Add(job.Id, job);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<DownloadJob?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.jobs.GetValueOrDefault(id));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DownloadJob>> ListAsync(IReadOnlyCollection<DownloadState>? states, int limit, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            return Task.FromResult<IReadOnlyList<DownloadJob>>(
                [.. this.jobs.Values.Where(job => states is null || states.Contains(job.State)).OrderByDescending(job => job.UpdatedAt).ThenBy(job => job.Id).Take(limit)]);
        }
    }

    /// <inheritdoc/>
    public Task<bool> UpdateAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            bool exists = this.jobs.ContainsKey(job.Id);
            if (exists)
            {
                this.jobs[job.Id] = job;
            }

            return Task.FromResult(exists);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            this.stored.RemoveAll(item => item.JobId == id);
            return Task.FromResult(this.jobs.Remove(id));
        }
    }

    /// <inheritdoc/>
    public Task<DownloadJob?> FindAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.jobs.Values.Where(job => job.Node == node && job.Role == role).OrderByDescending(job => job.CreatedAt).FirstOrDefault());
        }
    }

    /// <inheritdoc/>
    public Task<bool> TryTransitionAsync(DownloadJob job, DownloadState expected, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            bool matches = this.jobs.TryGetValue(job.Id, out DownloadJob? current) && current.State == expected;
            if (matches)
            {
                this.jobs[job.Id] = job;
            }

            return Task.FromResult(matches);
        }
    }

    /// <inheritdoc/>
    public Task AddItemsAsync(IReadOnlyList<DownloadItem> items, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            this.stored.AddRange(items);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DownloadItem>> ListItemsAsync(Guid jobId, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            return Task.FromResult<IReadOnlyList<DownloadItem>>([.. this.stored.Where(item => item.JobId == jobId).OrderBy(item => item.Position)]);
        }
    }

    /// <inheritdoc/>
    public Task<bool> UpdateItemAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            int index = this.stored.FindIndex(existing => existing.JobId == item.JobId && existing.Position == item.Position);
            if (index >= 0)
            {
                this.stored[index] = item;
            }

            return Task.FromResult(index >= 0);
        }
    }

    /// <inheritdoc/>
    public Task<DownloadItem?> FindStoredAsync(NodeRef node, AssetRole role, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            return Task.FromResult(this.stored.Find(item => item.Node == node && item.Role == role && item.IsStored));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListExclusiveFilesAsync(Guid jobId, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            HashSet<string> others = new(
                this.stored.Where(item => item.JobId != jobId && item.File is not null).Select(item => item.File!),
                StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyList<string>>(
                [.. this.stored.Where(item => item.JobId == jobId && item.File is not null && !others.Contains(item.File)).Select(item => item.File!).Distinct(StringComparer.Ordinal)]);
        }
    }
}
