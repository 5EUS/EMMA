using EMMA.Domain;

namespace EMMA.Application.Pipelines;

/// <summary>A download job and the leaves it stores.</summary>
/// <param name="Job">The job.</param>
/// <param name="Items">The leaves in the order the node tree lists them; empty until the job has walked its node.</param>
public sealed record DownloadDetail(DownloadJob Job, IReadOnlyList<DownloadItem> Items);
